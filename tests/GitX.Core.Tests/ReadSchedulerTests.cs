using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ReadSchedulerTests
{
    [Fact]
    public async Task Concurrent_reads_are_bounded_and_failures_release_their_slot()
    {
        var scheduler = new ReadScheduler(2);
        var started = Signal(); var finish = Signal();
        var active = 0; var peak = 0; var calls = 0;
        var reads = Enumerable.Range(0, 20).Select(_ => scheduler.RunAsync(async () =>
        {
            var count = Interlocked.Increment(ref active);
            Interlocked.Increment(ref calls);
            lock (scheduler) peak = Math.Max(peak, count);
            if (count == 2) started.TrySetResult();
            try { await finish.Task; return count; }
            finally { Interlocked.Decrement(ref active); }
        }, default)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref calls));
        finish.SetResult(); await Task.WhenAll(reads);
        Assert.Equal(2, peak); Assert.Equal(20, calls);
        await Assert.ThrowsAsync<IOException>(() => scheduler.Run<int>(() => throw new IOException("read failed"), default));
        Assert.Equal(42, await scheduler.Run(() => 42, default));
    }

    [Fact]
    public async Task Foreground_overtakes_queued_background_without_starving_it()
    {
        var scheduler = new ReadScheduler(1);
        using var first = await scheduler.AcquireAsync(default);
        var background = scheduler.AcquireAsync(default, ReadPriority.Background);
        var foreground = Enumerable.Range(0, 10).Select(_ => scheduler.AcquireAsync(default)).ToArray();
        first.Dispose();
        for (var i = 0; i < 8; i++)
        {
            using var lease = await foreground[i].WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(background.IsCompleted);
        }
        using (await background.WaitAsync(TimeSpan.FromSeconds(5))) Assert.False(foreground[8].IsCompleted);
        for (var i = 8; i < foreground.Length; i++) (await foreground[i].WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Queue_cancellation_removes_work_and_returns_capacity()
    {
        var scheduler = new ReadScheduler(1, 1);
        using var active = await scheduler.AcquireAsync(default);
        var executed = 0;
        for (var i = 0; i < 100; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = scheduler.Run(() => Interlocked.Increment(ref executed), cancellation.Token);
            await Assert.ThrowsAsync<IOException>(() => scheduler.AcquireAsync(default));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        Assert.Equal(0, executed);
        var next = scheduler.Run(() => 7, default); active.Dispose();
        Assert.Equal(7, await next);
    }

    [Fact]
    public async Task Cancelling_an_uninterruptible_read_does_not_release_its_execution_slot()
    {
        var scheduler = new ReadScheduler(1); var entered = Signal(); var returned = Signal();
        using var cancellation = new CancellationTokenSource();
        var native = scheduler.RunAsync(async () => { entered.SetResult(); await returned.Task; return 1; }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var nextEntered = Signal(); var next = scheduler.Run(() => { nextEntered.SetResult(); return 2; }, default);
        cancellation.Cancel();
        Assert.False(native.IsCompleted); Assert.False(nextEntered.Task.IsCompleted);
        returned.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native);
        Assert.Equal(2, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Available_slot_still_runs_the_synchronous_prefix_off_the_callers_context()
    {
        var scheduler = new ReadScheduler(1); var original = SynchronizationContext.Current;
        Task<SynchronizationContext?> read;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            read = scheduler.Run(() => SynchronizationContext.Current, default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        Assert.Null(await read);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
