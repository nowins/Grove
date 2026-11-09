using GitX.Core;
using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class BranchMenuTests
{
    [Fact]
    public async Task Ref_only_catalog_preserves_tracking_and_matches_actions_without_touching_worktree_or_index()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        repo.Git("remote", "add", "origin/team", "https://example.invalid/repo");
        repo.Git("update-ref", "refs/remotes/origin/team/topic", "HEAD");
        repo.Git("branch", "--track", "topic", "origin/team/topic");
        repo.Git("tag", "not-a-branch"); repo.Write("a", "dirty"); repo.Write("untracked", "keep");
        var context = await RepositoryActions.ReadAsync(repo.Root);
        var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")); var head = repo.Git("rev-parse", "HEAD");
        var branches = await BranchCatalog.ReadAsync(repo.Root);
        Assert.Equal(context.Branches, branches);
        Assert.Equal("origin/team/topic", branches.Single(b => b.Name == "topic").Upstream);
        Assert.Equal("origin/team", branches.Single(b => b.Remote).RemoteName);
        Assert.Single(branches, b => b.Current); Assert.Equal(3, branches.Count);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal("dirty", File.ReadAllText(Path.Combine(repo.Root, "a")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(repo.Root, "untracked")));
    }

    [Fact]
    public async Task Catalog_handles_unborn_detached_and_cancelled_reads()
    {
        using var repo = new TestRepository(); Assert.Empty(await BranchCatalog.ReadAsync(repo.Root));
        repo.Write("a", "base"); repo.Commit("base"); repo.Git("checkout", "--detach", "HEAD");
        Assert.False(Assert.Single(await BranchCatalog.ReadAsync(repo.Root)).Current);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BranchCatalog.ReadAsync(repo.Root, cancellation.Token));
    }

    [Fact]
    public async Task Busy_untracked_contents_do_not_prevent_opening_branch_list()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base"); repo.Write("busy", "still being written");
        using var busy = new FileStream(Path.Combine(repo.Root, "busy"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("main", Assert.Single(await BranchCatalog.ReadAsync(repo.Root)).Name);
        if (OperatingSystem.IsWindows()) await Assert.ThrowsAnyAsync<Exception>(() => RepositoryActions.ReadAsync(repo.Root));
    }

    [Fact]
    public async Task Opening_does_not_start_status_or_worktree_reads_and_actions_load_them_concurrently()
    {
        var context = new TaskCompletionSource<ActionContext>(); var worktrees = new TaskCompletionSource<WorktreeSnapshot>();
        var contextCalls = 0; var worktreeCalls = 0;
        using var session = new BranchMenuSession("root", (_, _) => Task.FromResult<IReadOnlyList<BranchEntry>>([Branch]),
            (_, _) => { contextCalls++; return context.Task; }, (_, _) => { worktreeCalls++; return worktrees.Task; });
        Assert.Equal(Branch, Assert.Single(await session.ReadAsync())); Assert.Equal(0, contextCalls); Assert.Equal(0, worktreeCalls);
        var pending = session.ReadActionsAsync(Branch);
        Assert.Equal(1, contextCalls); Assert.Equal(1, worktreeCalls); Assert.False(pending.IsCompleted);
        context.SetResult(Context()); Assert.False(pending.IsCompleted); worktrees.SetResult(Trees());
        Assert.Equal(Branch, Assert.Single((await pending).Context.Branches));
    }

    [Fact]
    public async Task Closing_cancels_ignored_late_reads_and_reopening_reads_fresh_refs()
    {
        var late = new TaskCompletionSource<IReadOnlyList<BranchEntry>>(); var token = CancellationToken.None;
        var session = new BranchMenuSession("old", (_, cancellation) => { token = cancellation; return late.Task; });
        var read = session.ReadAsync(); session.Dispose(); Assert.True(token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        late.SetResult([Branch]); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadAsync());
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        using (var first = new BranchMenuSession(repo.Root)) Assert.Single(await first.ReadAsync());
        repo.Git("branch", "new");
        using var second = new BranchMenuSession(repo.Root); Assert.Equal(2, (await second.ReadAsync()).Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_branch_or_mismatched_repository_rejects_action_context(bool changedBranch)
    {
        using var session = new BranchMenuSession("root", context: (_, _) => Task.FromResult(changedBranch ? Context() with { Branches = [Branch with { Sha = new('b', 40) }] } : Context() with { Root = "other" }),
            worktrees: (_, _) => Task.FromResult(Trees()));
        await Assert.ThrowsAsync<IOException>(() => session.ReadActionsAsync(Branch));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Back_or_close_retires_pending_action_page_even_if_read_ignores_cancellation(bool close)
    {
        var late = new TaskCompletionSource<ActionContext>();
        using var session = new BranchMenuSession("root", context: (_, _) => late.Task, worktrees: (_, _) => Task.FromResult(Trees()));
        var pending = session.ReadActionsAsync(Branch);
        if (close) session.Dispose(); else session.CancelActions();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); late.SetResult(Context());
    }

    [Fact]
    public async Task Closing_picker_does_not_cancel_authorized_write_and_writer_rechecks_stale_state()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var session = new BranchMenuSession(repo.Root); var target = Assert.Single(await session.ReadAsync());
        var ready = await session.ReadActionsAsync(target); session.Dispose();
        await new BranchWriter().ExecuteAsync(ready.Context, BranchAction.Create, name: "from-picker");
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
        using var next = new BranchMenuSession(repo.Root); ready = await next.ReadActionsAsync(target); repo.Write("a", "external");
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(ready.Context, BranchAction.Create, name: "stale"));
        Assert.Empty(repo.Git("branch", "--list", "stale")); Assert.Equal("external", File.ReadAllText(Path.Combine(repo.Root, "a")));
    }

    private static readonly BranchEntry Branch = new("main", "refs/heads/main", new('a', 40), false, true, null);
    private static ActionContext Context() => new("root", "token", Branch.Sha, "main", "", false, false, [Branch], []);
    private static WorktreeSnapshot Trees() => new("root", "common", []);
}
