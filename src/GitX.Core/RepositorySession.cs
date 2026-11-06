namespace GitX.Core;

// A session belongs to one opened worktree. Its owner serializes refreshes;
// invalidations arriving during a read are retained for the next pass. Replacing
// the session isolates a late result even when the reader ignores cancellation.
public sealed class RepositorySession(IRepositoryReader reader, RepositorySnapshot snapshot)
{
    private readonly object _gate = new();
    private RepositoryChangeSet? _pending;
    private bool _reading;
    public RepositorySnapshot Snapshot { get; private set; } = snapshot;
    public bool HasPending { get { lock (_gate) return _pending is not null; } }

    public void Invalidate(RepositoryChangeSet changes)
    {
        lock (_gate) _pending = _pending?.Merge(changes) ?? changes;
    }

    public async Task<RepositoryRefreshResult> RefreshAsync(CancellationToken cancellation = default)
    {
        RepositoryChangeSet changes;
        lock (_gate)
        {
            if (_reading) throw new InvalidOperationException("仓库会话不能并发刷新。");
            changes = _pending ?? throw new InvalidOperationException("仓库会话没有待处理的变化。");
            _pending = null; _reading = true;
        }
        try
        {
            var result = await reader.RefreshAsync(Snapshot, changes, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (result.Snapshot.Root != Snapshot.Root) throw new IOException("刷新结果不属于当前仓库。");
            Snapshot = result.Snapshot;
            return result;
        }
        catch
        {
            // A failed pass may have consumed only part of a batch. The next
            // request must reconcile fully, rather than treating it as complete.
            Invalidate(RepositoryChangeSet.Full);
            throw;
        }
        finally { lock (_gate) _reading = false; }
    }
}
