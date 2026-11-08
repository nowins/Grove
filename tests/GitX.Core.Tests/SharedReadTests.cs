using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class SharedReadTests
{
    [Fact]
    public async Task Overlapping_inputs_share_one_execution_but_finished_results_are_not_cached()
    {
        var reads = new SharedRead<string, object>(4, 100);
        var finish = Signal<object>(); var calls = 0;
        Task<object> Read(CancellationToken token) { calls++; return finish.Task; }
        var waiting = Enumerable.Range(0, 16).Select(_ => reads.RunAsync("input", 10, Read, default)).ToArray();
        Assert.Equal(1, calls); var result = new object(); finish.SetResult(result);
        foreach (var value in await Task.WhenAll(waiting)) Assert.Same(result, value);
        var fresh = new object();
        Assert.Same(fresh, await reads.RunAsync("input", 10, _ => { calls++; return Task.FromResult(fresh); }, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task One_subscriber_can_cancel_without_cancelling_another()
    {
        var reads = new SharedRead<string, int>(4, 100); var finish = Signal<int>();
        using var first = new CancellationTokenSource(); CancellationToken shared = default;
        var a = reads.RunAsync("same", 1, token => { shared = token; return finish.Task; }, first.Token);
        var b = reads.RunAsync("same", 1, _ => throw new Exception("duplicate"), default);
        first.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(shared.IsCancellationRequested); Assert.False(b.IsCompleted);
        finish.SetResult(42); Assert.Equal(42, await b);
    }

    [Fact]
    public async Task Last_cancellation_detaches_work_and_its_late_completion_cannot_remove_a_replacement()
    {
        var reads = new SharedRead<string, int>(4, 100);
        var oldWork = new TaskCompletionSource<int>(); var newWork = Signal<int>();
        using var cancellation = new CancellationTokenSource(); CancellationToken shared = default;
        var old = reads.RunAsync("same", 1, token => { shared = token; return oldWork.Task; }, cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.True(shared.IsCancellationRequested);
        var calls = 0;
        Task<int> Replacement(CancellationToken _) { calls++; return newWork.Task; }
        var newer = reads.RunAsync("same", 1, Replacement, default);
        oldWork.SetResult(1); // An uninterruptible old operation has actually returned.
        await ReturnedAsync(reads, 1);
        var newest = reads.RunAsync("same", 1, Replacement, default);
        Assert.Equal(1, calls); newWork.SetResult(2);
        Assert.Equal(new[] { 2, 2 }, await Task.WhenAll(newer, newest));
    }

    [Fact]
    public async Task Failure_is_shared_then_removed_so_the_same_input_can_retry()
    {
        var reads = new SharedRead<string, int>(4, 100); var finish = Signal<int>();
        var a = reads.RunAsync("same", 1, _ => finish.Task, default);
        var b = reads.RunAsync("same", 1, _ => throw new Exception("duplicate"), default);
        finish.SetException(new IOException("read failed"));
        Assert.Equal("read failed", (await Assert.ThrowsAsync<IOException>(() => a)).Message);
        Assert.Equal("read failed", (await Assert.ThrowsAsync<IOException>(() => b)).Message);
        Assert.Equal(3, await reads.RunAsync("same", 1, _ => Task.FromResult(3), default));
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(4, 10)]
    public async Task Detached_running_work_keeps_its_sharing_budget_until_it_returns(int capacity, long bytes)
    {
        var reads = new SharedRead<string, int>(capacity, bytes);
        var oldWork = new TaskCompletionSource<int>(); var otherWork = Signal<int>();
        using var cancellation = new CancellationTokenSource();
        var old = reads.RunAsync("old", 10, _ => oldWork.Task, cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal((1, 0, 10L), reads.Status);
        var calls = 0;
        Task<int> Other(CancellationToken _) { calls++; return otherWork.Task; }
        var a = reads.RunAsync("other", 10, Other, default);
        var b = reads.RunAsync("other", 10, Other, default);
        Assert.Equal(2, calls); otherWork.SetResult(7);
        Assert.Equal(new[] { 7, 7 }, await Task.WhenAll(a, b));
        oldWork.SetResult(1);
        await ReturnedAsync(reads, 0);
        var pending = Signal<int>(); calls = 0;
        Task<int> Fresh(CancellationToken _) { calls++; return pending.Task; }
        var c = reads.RunAsync("fresh", 10, Fresh, default);
        var d = reads.RunAsync("fresh", 10, Fresh, default);
        Assert.Equal(1, calls); pending.SetResult(8);
        Assert.Equal(new[] { 8, 8 }, await Task.WhenAll(c, d));
    }

    [Fact]
    public async Task Subscriber_limit_and_precancelled_call_do_not_start_work_and_capacity_is_reusable()
    {
        var reads = new SharedRead<string, int>(4, 100, 1); var finish = Signal<int>();
        using var cancellation = new CancellationTokenSource();
        var a = reads.RunAsync("same", 1, _ => finish.Task, cancellation.Token);
        await Assert.ThrowsAsync<IOException>(() => reads.RunAsync("same", 1, _ => throw new Exception("unexpected"), default));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reads.RunAsync("new", 1, _ => throw new Exception("unexpected"), cancellation.Token));
        Assert.Equal(2, await reads.RunAsync("new", 1, _ => Task.FromResult(2), default)); finish.SetResult(1);
    }

    [Fact]
    public async Task Completion_and_last_subscriber_cancellation_can_race_without_disposed_token_errors()
    {
        var reads = new SharedRead<string, int>(4, 100);
        for (var i = 0; i < 100; i++)
        {
            using var cancellation = new CancellationTokenSource(); var finish = Signal<int>();
            var read = reads.RunAsync("input", 1, _ => finish.Task, cancellation.Token);
            await Task.WhenAll(Task.Run(() => cancellation.Cancel()), Task.Run(() => finish.SetResult(1)));
            try { Assert.Equal(1, await read); } catch (OperationCanceledException) { }
        }
        Assert.Equal(2, await reads.RunAsync("input", 1, _ => Task.FromResult(2), default));
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ReturnedAsync(SharedRead<string, int> reads, int running)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (reads.Status.Running != running) await Task.Delay(1, deadline.Token);
    }
}
