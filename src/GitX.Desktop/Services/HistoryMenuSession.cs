using GitX.Core;

namespace GitX.Desktop.Services;

internal sealed record HistoryMenuState(string Root, string? Head, string Branch, bool Detached, bool Dirty, bool InOperation)
{
    public static HistoryMenuState From(RepositorySnapshot snapshot)
    {
        var branch = snapshot.References?.FirstOrDefault(r => r.Current && r.Kind == "local");
        return new(snapshot.Root, snapshot.Head, branch?.Name ?? snapshot.Branch, snapshot.Head is not null && branch is null,
            snapshot.Changes.Count > 0, snapshot.Operation is not null || snapshot.Changes.Any(c => c.Kind == ComparisonKind.Conflict));
    }
}

// Closing the popup retires metadata reads; a chosen action owns a separate read
// lifetime. Neither token is passed to an actual Git write.
internal sealed class HistoryMenuSession : IDisposable
{
    private readonly HistoryMenuState _state;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<BranchEntry>>> _branches;
    private readonly Func<string, CancellationToken, Task<ActionContext>> _context;
    private readonly CancellationTokenSource _popup = new(), _action = new();
    private readonly CancellationToken _popupToken, _actionToken;
    private bool _closed, _disposed;

    public HistoryMenuSession(HistoryMenuState state,
        Func<string, CancellationToken, Task<IReadOnlyList<BranchEntry>>>? branches = null,
        Func<string, CancellationToken, Task<ActionContext>>? context = null)
    {
        _state = state; _popupToken = _popup.Token; _actionToken = _action.Token;
        _branches = branches ?? BranchCatalog.ReadAsync; _context = context ?? RepositoryActions.ReadAsync;
    }

    public async Task<IReadOnlyList<BranchEntry>> ReadBranchesAsync()
    {
        _popupToken.ThrowIfCancellationRequested();
        var result = await _branches(_state.Root, _popupToken).WaitAsync(_popupToken);
        _popupToken.ThrowIfCancellationRequested();
        return result;
    }

    public async Task<ActionContext> ReadContextAsync()
    {
        _actionToken.ThrowIfCancellationRequested();
        var result = await _context(_state.Root, _actionToken).WaitAsync(_actionToken);
        _actionToken.ThrowIfCancellationRequested();
        if (result.Root != _state.Root || result.Head != _state.Head || result.Detached != _state.Detached ||
            !result.Detached && result.Branch != _state.Branch)
            throw new IOException("仓库或当前修订已变化，请重新打开提交菜单。");
        return result;
    }

    public void ClosePopup() { if (_closed) return; _closed = true; _popup.Cancel(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ClosePopup(); _action.Cancel(); _popup.Dispose(); _action.Dispose();
    }
}
