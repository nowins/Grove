using System.Diagnostics;

namespace GitX.Core;

// Shares only overlapping work on immutable inputs. Finished values and failures
// are never cached. The factory must use the normal execution scheduler.
internal sealed class SharedRead<TKey, TValue>(int capacity, long byteCapacity, int waiterCapacity = 256) where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = new();
    private int _running, _waiters;
    private long _bytes;
    internal (int Running, int Waiters, long Bytes) Status
    {
        get { lock (_gate) return (_running, _waiters, _bytes); }
    }

    private sealed class Entry(long bytes)
    {
        public readonly long Bytes = bytes;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskCompletionSource<TValue> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters;
        public bool Finished, Cancelling, Disposed;
    }

    public async Task<TValue> RunAsync(TKey key, long bytes, Func<CancellationToken, Task<TValue>> factory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        Entry? entry; var start = false;
        lock (_gate)
        {
            if (_waiters >= waiterCapacity) throw new IOException("等待中的读取过多，请稍后重试。");
            if (!_entries.TryGetValue(key, out entry) && _running < capacity && bytes <= byteCapacity - _bytes)
            {
                entry = new(bytes); _entries.Add(key, entry);
                _running++; _bytes += bytes; start = true;
            }
            if (entry is not null) { entry.Waiters++; _waiters++; }
        }
        // The sharing budget is not a document size limit. Overflow still uses
        // the existing bounded execution path, without retaining another key.
        if (entry is null) return await factory(token).ConfigureAwait(false);
        if (start) _ = ExecuteAsync(key, entry, factory);
        try
        {
            var value = await entry.Completion.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return value;
        }
        finally { Leave(key, entry); }
    }

    private async Task ExecuteAsync(TKey key, Entry entry, Func<CancellationToken, Task<TValue>> factory)
    {
        try
        {
            var value = await factory(entry.Cancellation.Token).ConfigureAwait(false);
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            Finish(key, entry);
            entry.Completion.TrySetResult(value);
        }
        catch (OperationCanceledException)
        {
            Finish(key, entry);
            entry.Completion.TrySetCanceled();
        }
        catch (Exception error)
        {
            Finish(key, entry);
            entry.Completion.TrySetException(error);
            _ = entry.Completion.Task.Exception; // Observe faults after the last subscriber has left.
        }
    }

    private void Finish(TKey key, Entry entry)
    {
        bool dispose;
        lock (_gate)
        {
            Remove(key, entry);
            entry.Finished = true; _running--; _bytes -= entry.Bytes;
            dispose = TakeDisposal(entry);
        }
        if (dispose) entry.Cancellation.Dispose();
    }

    private void Leave(TKey key, Entry entry)
    {
        bool cancel, dispose;
        lock (_gate)
        {
            _waiters--; entry.Waiters--;
            cancel = entry.Waiters == 0 && !entry.Finished;
            if (entry.Waiters == 0) Remove(key, entry);
            if (cancel) entry.Cancelling = true;
            dispose = TakeDisposal(entry);
        }
        if (cancel)
        {
            // Never invoke callbacks under the dictionary lock. Keep the CTS
            // alive if work finishes concurrently with cancellation callbacks.
            try { entry.Cancellation.Cancel(); }
            catch (AggregateException error) { Trace.TraceWarning("共享读取取消回调失败：{0}", error.GetType().Name); }
            finally
            {
                lock (_gate) { entry.Cancelling = false; dispose = TakeDisposal(entry); }
            }
        }
        if (dispose) entry.Cancellation.Dispose();
    }

    private void Remove(TKey key, Entry entry)
    {
        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(key);
    }

    private static bool TakeDisposal(Entry entry)
    {
        if (!entry.Finished || entry.Waiters != 0 || entry.Cancelling || entry.Disposed) return false;
        return entry.Disposed = true;
    }
}
