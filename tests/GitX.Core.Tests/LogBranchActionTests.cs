using GitX.Core;
using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class LogBranchActionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_preserves_commit_tracking_dirty_index_workfiles_and_other_worktree(bool current)
    {
        using var repo = Base(); repo.Git("branch", "topic");
        repo.Git("remote", "add", "origin", "https://example.invalid/repo"); repo.Git("update-ref", "refs/remotes/origin/tracked", "HEAD");
        var name = current ? "main" : "topic"; repo.Git("branch", "--set-upstream-to=origin/tracked", name);
        var other = Path.Combine(repo.Container, "other"); repo.Git("worktree", "add", "-b", "other", other);
        var otherIndex = IndexAt(repo, other); var head = Head(repo);
        repo.Write("a", "staged"); repo.Git("add", "a"); repo.Write("a", "unstaged"); repo.Write("untracked", "keep");
        var context = await RepositoryActions.ReadAsync(repo.Root); var index = IndexAt(repo, repo.Root);
        await new BranchWriter().ExecuteAsync(context, BranchAction.Rename, context.Branches.Single(b => b.Name == name), "renamed/中文");
        Assert.Equal(current ? "renamed/中文" : "main", repo.Git("branch", "--show-current").Trim()); Assert.Equal(head, Head(repo));
        Assert.Equal(head, repo.Git("rev-parse", "refs/heads/renamed/中文").Trim()); Assert.Empty(repo.Git("for-each-ref", "--format=%(refname)", "refs/heads/" + name));
        Assert.Equal("refs/remotes/origin/tracked", repo.Git("for-each-ref", "--format=%(upstream)", "refs/heads/renamed/中文").Trim());
        Assert.Equal(index, IndexAt(repo, repo.Root)); Assert.Equal("unstaged", Read(repo, "a")); Assert.Equal("keep", Read(repo, "untracked"));
        Assert.Equal(otherIndex, IndexAt(repo, other)); Assert.Equal(head, repo.Git("-C", other, "rev-parse", "HEAD").Trim());
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(other, "a")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_refuses_branch_checked_out_in_another_worktree_without_touching_it(bool current)
    {
        using var repo = Base(); repo.Git("branch", "occupied"); var name = current ? "main" : "occupied";
        var other = Path.Combine(repo.Container, "other"); repo.Git("worktree", "add", "--force", other, name);
        File.WriteAllText(Path.Combine(other, "a"), "other dirty"); var otherIndex = IndexAt(repo, other);
        var context = await RepositoryActions.ReadAsync(repo.Root); var target = context.Branches.Single(b => b.Name == name); var head = Head(repo);
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(context, BranchAction.Rename, target, "renamed"));
        Assert.Equal(name, repo.Git("-C", other, "branch", "--show-current").Trim()); Assert.Equal(head, Head(repo));
        Assert.Equal(otherIndex, IndexAt(repo, other)); Assert.Equal("other dirty", File.ReadAllText(Path.Combine(other, "a")));
        Assert.Equal(head, repo.Git("rev-parse", target.CanonicalName).Trim()); Assert.Empty(repo.Git("branch", "--list", "renamed"));
    }

    [Fact]
    public async Task Rename_rejects_invalid_colliding_remote_foreign_and_stale_targets()
    {
        using var repo = Base(); repo.Git("branch", "existing"); repo.Git("update-ref", "refs/remotes/origin/topic", "HEAD");
        var writer = new BranchWriter(); var context = await RepositoryActions.ReadAsync(repo.Root); var target = context.Branches.Single(b => b.Current);
        var head = Head(repo); var index = IndexAt(repo, repo.Root);
        foreach (var name in new[] { "", "-unsafe", "@{-1}", "bad..name", "EXISTING", "main" })
            await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, BranchAction.Rename, target, name));
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, BranchAction.Rename, context.Branches.Single(b => b.Remote), "local"));
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, BranchAction.Rename, target with { Sha = new('a', 40) }, "foreign"));
        Assert.Equal(head, Head(repo)); Assert.Equal(index, IndexAt(repo, repo.Root));
        repo.Git("branch", "external"); await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, BranchAction.Rename, target, "stale"));
        Assert.Empty(repo.Git("branch", "--list", "stale")); Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
    }

    [Fact]
    public async Task Availability_allows_current_dirty_rename_but_refuses_remote_and_occupied_targets()
    {
        using var repo = Base(); repo.Write("a", "dirty"); var context = await RepositoryActions.ReadAsync(repo.Root); var branch = context.Branches.Single();
        Assert.Null(BranchActionAvailability.DisabledReason(context, BranchAction.Rename, branch));
        Assert.NotNull(BranchActionAvailability.DisabledReason(context with { InOperation = true }, BranchAction.Rename, branch));
        Assert.NotNull(BranchActionAvailability.DisabledReason(context, BranchAction.Rename, branch with { Remote = true }));
        var worktree = new WorktreeEntry("elsewhere", branch.Sha, branch.CanonicalName, false, false, null, null, false, false);
        Assert.NotNull(BranchActionAvailability.DisabledReason(context, BranchAction.Rename, branch, [worktree]));
    }

    [Fact]
    public async Task Rebase_uses_exact_selected_sha_without_moving_other_refs_or_other_worktree()
    {
        using var repo = Diverged(out var target); var before = Head(repo); repo.Git("branch", "keep"); repo.Git("config", "rebase.updateRefs", "true");
        var onto = repo.Git("rev-parse", "onto").Trim(); var other = Path.Combine(repo.Container, "other"); repo.Git("worktree", "add", "-b", "other", other);
        File.WriteAllText(Path.Combine(other, "other.txt"), "keep dirty"); var index = IndexAt(repo, other);
        await new CommitReferenceWriter().RebaseOntoAsync(await RepositoryActions.ReadAsync(repo.Root), target);
        Assert.Equal(target, repo.Git("rev-parse", "HEAD^").Trim()); Assert.Equal("local\n", Read(repo, "local.txt")); Assert.Equal("target\n", Read(repo, "target.txt"));
        Assert.False(File.Exists(Path.Combine(repo.Root, "tip-only.txt"))); Assert.Equal(onto, repo.Git("rev-parse", "onto").Trim()); Assert.Equal(before, repo.Git("rev-parse", "keep").Trim());
        Assert.Empty(repo.Git("--no-optional-locks", "status", "--porcelain")); Assert.Contains(before, repo.Git("for-each-ref", "--format=%(objectname)", "refs/gitx/recovery/"));
        Assert.Equal(before, repo.Git("-C", other, "rev-parse", "HEAD").Trim()); Assert.Equal(index, IndexAt(repo, other)); Assert.Equal("keep dirty", File.ReadAllText(Path.Combine(other, "other.txt")));
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("skip")]
    [InlineData("abort")]
    public async Task Rebase_to_commit_supports_existing_conflict_controls(string control)
    {
        using var repo = Base(); repo.Git("checkout", "-b", "onto"); repo.Write("a", "target\n"); repo.Commit("target"); var target = Head(repo);
        repo.Git("checkout", "main"); repo.Write("a", "local\n"); repo.Commit("local"); repo.Write("last", "last\n"); repo.Commit("last"); var before = Head(repo);
        var output = await new CommitReferenceWriter().RebaseOntoAsync(await RepositoryActions.ReadAsync(repo.Root), target); Assert.Contains("冲突", output);
        if (control == "continue") { repo.Write("a", "resolved\n"); repo.Git("add", "a"); }
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, control == "abort", control == "skip");
        snapshot = await reader.OpenAsync(repo.Root); Assert.Null(snapshot.Operation); Assert.Empty(repo.Git("ls-files", "-u"));
        Assert.Equal(control == "abort" ? "local\n" : control == "skip" ? "target\n" : "resolved\n", Read(repo, "a")); Assert.Equal("last\n", Read(repo, "last"));
        Assert.Equal(target, repo.Git("rev-parse", "onto").Trim()); if (control == "abort") Assert.Equal(before, Head(repo));
    }

    [Fact]
    public async Task Rebase_rejects_dirty_invalid_current_and_stale_states_before_moving_head()
    {
        using var repo = Diverged(out var target); var head = Head(repo); var writer = new CommitReferenceWriter(); var context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => writer.RebaseOntoAsync(context, "HEAD")); await Assert.ThrowsAsync<IOException>(() => writer.RebaseOntoAsync(context, head));
        repo.Write("untracked", "keep"); await Assert.ThrowsAsync<IOException>(() => writer.RebaseOntoAsync(context, target));
        context = await RepositoryActions.ReadAsync(repo.Root); await Assert.ThrowsAsync<IOException>(() => writer.RebaseOntoAsync(context, target));
        Assert.Equal(head, Head(repo)); Assert.Equal("keep", Read(repo, "untracked")); Assert.Empty(repo.Git("for-each-ref", "--format=%(refname)", "refs/gitx/recovery/"));
    }

    [Fact]
    public async Task Rebase_in_detached_worktree_keeps_original_local_branch()
    {
        using var repo = Diverged(out var target); var main = Head(repo); repo.Git("checkout", "--detach", "main");
        await new CommitReferenceWriter().RebaseOntoAsync(await RepositoryActions.ReadAsync(repo.Root), target);
        Assert.Equal(target, repo.Git("rev-parse", "HEAD^").Trim()); Assert.Empty(repo.Git("branch", "--show-current")); Assert.Equal(main, repo.Git("rev-parse", "main").Trim());
    }

    [Fact]
    public async Task Rebase_refuses_current_branch_shared_with_another_worktree()
    {
        using var repo = Diverged(out var target); var before = Head(repo); var other = Path.Combine(repo.Container, "other"); repo.Git("worktree", "add", "--force", other, "main"); var index = IndexAt(repo, other);
        var context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => new CommitReferenceWriter().RebaseOntoAsync(context, target));
        Assert.Equal(before, Head(repo)); Assert.Equal(before, repo.Git("-C", other, "rev-parse", "HEAD").Trim()); Assert.Equal(index, IndexAt(repo, other));
    }

    private static TestRepository Base() { var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base"); return repo; }
    private static TestRepository Diverged(out string target)
    {
        var repo = Base(); repo.Git("checkout", "-b", "onto"); repo.Write("target.txt", "target\n"); repo.Commit("target"); target = Head(repo);
        repo.Write("tip-only.txt", "not selected\n"); repo.Commit("later target tip"); repo.Git("checkout", "main"); repo.Write("local.txt", "local\n"); repo.Commit("local"); return repo;
    }
    private static string Head(TestRepository repo) => repo.Git("rev-parse", "HEAD").Trim();
    private static byte[] IndexAt(TestRepository repo, string root) => File.ReadAllBytes(repo.Git("-C", root, "rev-parse", "--path-format=absolute", "--git-path", "index").Trim());
    private static string Read(TestRepository repo, string path) => File.ReadAllText(Path.Combine(repo.Root, path));
}
