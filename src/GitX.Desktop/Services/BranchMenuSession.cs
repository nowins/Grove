using GitX.Core;

namespace GitX.Desktop.Services;

internal sealed class BranchMenuSession : IDisposable
{
    private readonly string _root;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<BranchEntry>>> _branches;
    private readonly Func<string, CancellationToken, Task<ActionContext>> _context;
    private readonly Func<string, CancellationToken, Task<WorktreeSnapshot>> _worktrees;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _action;
    private readonly CancellationToken _token;
    private bool _disposed;

    public BranchMenuSession(string root,
        Func<string, CancellationToken, Task<IReadOnlyList<BranchEntry>>>? branches = null,
        Func<string, CancellationToken, Task<ActionContext>>? context = null,
        Func<string, CancellationToken, Task<WorktreeSnapshot>>? worktrees = null)
    {
        _root = root; _token = _lifetime.Token;
        _branches = branches ?? BranchCatalog.ReadAsync;
        _context = context ?? RepositoryActions.ReadAsync;
        _worktrees = worktrees ?? WorktreeCatalog.ReadAsync;
    }

    public async Task<IReadOnlyList<BranchEntry>> ReadAsync()
    {
        _token.ThrowIfCancellationRequested();
        var result = await _branches(_root, _token).WaitAsync(_token);
        _token.ThrowIfCancellationRequested();
        return result;
    }

    public async Task<(ActionContext Context, WorktreeSnapshot Worktrees)> ReadActionsAsync(BranchEntry? selected)
    {
        _token.ThrowIfCancellationRequested();
        CancelActions(); _action = CancellationTokenSource.CreateLinkedTokenSource(_token);
        var token = _action.Token;
        var context = _context(_root, token); var worktrees = _worktrees(_root, token);
        await Task.WhenAll(context, worktrees).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (context.Result.Root != _root || worktrees.Result.Root != _root || selected is not null && !context.Result.Branches.Contains(selected))
            throw new IOException("分支已变化，请重新打开分支菜单。");
        return (context.Result, worktrees.Result);
    }

    public void CancelActions() { _action?.Cancel(); _action?.Dispose(); _action = null; }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); CancelActions(); _lifetime.Dispose(); }
}
