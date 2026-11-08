namespace GitX.Core;

internal enum ReadPriority { Foreground, Background }

// Limits execution, not repository/file sizes. A cancelled native call keeps
// its lease until it actually returns. Queued cancellations remove their node.
internal sealed class ReadScheduler(int concurrency, int queueCapacity = 256)
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _foreground = new(), _background = new();
    private int _active, _foregroundTurns;

    private sealed class Waiter
    {
        public readonly TaskCompletionSource<Lease> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
    }

    internal sealed class Lease(ReadScheduler owner) : IDisposable
    {
        private ReadScheduler? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    public async Task<Lease> AcquireAsync(CancellationToken token, ReadPriority priority = ReadPriority.Foreground)
    {
        token.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            if (concurrency < 1 || queueCapacity < 1) throw new InvalidOperationException("读取调度额度必须大于零。");
            if (_active < concurrency) { _active++; return new(this); }
            if (_foreground.Count + _background.Count >= queueCapacity) throw new IOException("等待中的读取过多，请稍后重试。");
            waiter = new();
            waiter.Node = (priority == ReadPriority.Background ? _background : _foreground).AddLast(waiter);
        }
        using var registration = token.Register(() => Cancel(waiter, token));
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        lock (_gate)
        {
            // A granted lease is released by its owner, even when cancellation
            // races with the grant. RunAsync checks the token before executing.
            if (waiter.Node?.List is not { } queue) return;
            queue.Remove(waiter.Node);
            waiter.Completion.TrySetCanceled(token);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _active--;
            var queue = _foreground.Count > 0 && (_background.Count == 0 || _foregroundTurns < 8) ? _foreground : _background;
            if (queue.First is not { } next) { _foregroundTurns = 0; return; }
            queue.RemoveFirst();
            _foregroundTurns = queue == _foreground ? Math.Min(8, _foregroundTurns + 1) : 0;
            _active++;
            next.Value.Completion.SetResult(new(this));
        }
    }

    public Task<T> Run<T>(Func<T> action, CancellationToken token, ReadPriority priority = ReadPriority.Foreground)
        => RunAsync(() => Task.FromResult(action()), token, priority);

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token, ReadPriority priority = ReadPriority.Foreground)
    {
        using var lease = await AcquireAsync(token, priority).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var result = await action().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }, CancellationToken.None).ConfigureAwait(false);
    }
}

internal static class ReadWork
{
    // CPU and Git have separate leases: a large Diff can await its Git fallback
    // without recursively waiting for the CPU slot that it already owns.
    internal static readonly ReadScheduler Cpu = new(Math.Min(2, Environment.ProcessorCount));
    internal static readonly ReadScheduler Processes = new(2);
}
