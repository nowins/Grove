using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class BranchTests
{
    [Fact]
    public async Task Create_switch_delete_and_dirty_or_stale_protection()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var writer = new BranchWriter(); var context = await RepositoryActions.ReadAsync(repo.Root);
        await writer.ExecuteAsync(context, BranchAction.Create, name: "feature/中文");
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, BranchAction.Create, name: "stale"));
        context = await RepositoryActions.ReadAsync(repo.Root); var target = context.Branches.Single(b => b.Name == "feature/中文");
        await writer.ExecuteAsync(context, BranchAction.Switch, target);
        Assert.Equal("feature/中文", repo.Git("branch", "--show-current").Trim());
        repo.Write("a", "dirty"); context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, BranchAction.Switch, context.Branches.Single(b => b.Name == "main")));
        Assert.Equal("dirty", File.ReadAllText(Path.Combine(repo.Root, "a")));
        repo.Git("restore", "a"); context = await RepositoryActions.ReadAsync(repo.Root);
        await writer.ExecuteAsync(context, BranchAction.Switch, context.Branches.Single(b => b.Name == "main"));
        context = await RepositoryActions.ReadAsync(repo.Root);
        await writer.ExecuteAsync(context, BranchAction.Delete, context.Branches.Single(b => b.Name == "feature/中文"));
        Assert.DoesNotContain("feature/中文", repo.Git("branch", "--list"));
    }

    [Theory]
    [InlineData(BranchAction.Merge, "merge")]
    [InlineData(BranchAction.Rebase, "rebase")]
    public async Task Conflicts_enter_existing_merge_workflow(BranchAction action, string kind)
    {
        using var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Write("a", "topic\n"); repo.Commit("topic");
        repo.Git("checkout", "main"); repo.Write("a", "main\n"); repo.Commit("main");
        var context = await RepositoryActions.ReadAsync(repo.Root);
        var result = await new BranchWriter().ExecuteAsync(context, action, context.Branches.Single(b => b.Name == "topic"));
        Assert.Contains("冲突", result);
        var snapshot = await new RepositoryReader().OpenAsync(repo.Root);
        Assert.Equal(kind, snapshot.Operation!.Kind); Assert.NotEmpty(repo.Git("ls-files", "-u"));
        await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation, true);
        Assert.Empty(repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Unmerged_or_other_worktree_branch_cannot_be_deleted_or_switched()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Write("a", "topic"); repo.Commit("topic"); repo.Git("checkout", "main");
        var context = await RepositoryActions.ReadAsync(repo.Root); var writer = new BranchWriter();
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, BranchAction.Delete, context.Branches.Single(b => b.Name == "topic")));
        repo.Git("worktree", "add", Path.Combine(repo.Container, "linked"), "topic"); context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, BranchAction.Switch, context.Branches.Single(b => b.Name == "topic")));
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
    }
}
