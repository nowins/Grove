using GitX.Core;
using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RemoteBranchCheckoutTests
{
    private static TestRepository Fixture(params string[] remotes)
    {
        var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base");
        foreach (var remote in remotes)
        {
            repo.Git("remote", "add", remote, repo.Root);
            repo.Git("update-ref", $"refs/remotes/{remote}/feature/topic", "HEAD");
        }
        return repo;
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("team/nested")]
    public async Task Checkout_creates_short_local_name_and_explicit_tracking(string remote)
    {
        using var repo = Fixture(remote);
        var context = await RepositoryActions.ReadAsync(repo.Root);
        var target = context.Branches.Single(b => b.Name == remote + "/feature/topic");
        Assert.Equal(remote, target.RemoteName);
        Assert.Equal("feature/topic", BranchCheckout.RemoteBranchName(target));
        await new BranchWriter().ExecuteAsync(context, BranchAction.Switch, target);
        Assert.Equal("feature/topic", repo.Git("branch", "--show-current").Trim());
        Assert.Equal(target.Sha, repo.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(remote, repo.Git("config", "branch.feature/topic.remote").Trim());
        Assert.Equal("refs/heads/feature/topic", repo.Git("config", "branch.feature/topic.merge").Trim());
        Assert.Empty(repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Tracking_a_local_branch_with_a_remote_like_name_does_not_match_the_remote_ref()
    {
        using var repo = Fixture("origin");
        repo.Git("branch", "--no-track", "origin/feature/topic", "HEAD");
        repo.Git("branch", "--track", "local-tracker", "refs/heads/origin/feature/topic");
        var context = await RepositoryActions.ReadAsync(repo.Root); var target = context.Branches.Single(b => b.Remote);
        Assert.Equal("refs/heads/origin/feature/topic", context.Branches.Single(b => b.Name == "local-tracker").UpstreamCanonicalName);
        var plan = BranchCheckout.Resolve(context, target);
        Assert.True(plan.CreateTracking); Assert.Equal("feature/topic", plan.LocalName);
        await new BranchWriter().ExecuteAsync(context, BranchAction.Switch, target);
        Assert.Equal("feature/topic", repo.Git("branch", "--show-current").Trim());
        Assert.Equal("origin", repo.Git("config", "branch.feature/topic.remote").Trim());
    }

    [Fact]
    public async Task Existing_custom_tracking_branch_preserves_its_commits_and_configuration()
    {
        using var repo = Fixture("origin");
        repo.Git("switch", "--track", "-c", "my-topic", "origin/feature/topic");
        repo.Write("a", "local progress\n"); repo.Commit("local progress");
        var localSha = repo.Git("rev-parse", "HEAD").Trim(); repo.Git("switch", "main");
        var context = await RepositoryActions.ReadAsync(repo.Root);
        var target = context.Branches.Single(b => b.Remote);
        await new BranchWriter().ExecuteAsync(context, BranchAction.Switch, target);
        Assert.Equal("my-topic", repo.Git("branch", "--show-current").Trim());
        Assert.Equal(localSha, repo.Git("rev-parse", "HEAD").Trim());
        Assert.Equal("origin/feature/topic", repo.Git("rev-parse", "--abbrev-ref", "@{upstream}").Trim());
        Assert.Equal("local progress\n", File.ReadAllText(Path.Combine(repo.Root, "a")));
    }

    [Fact]
    public async Task Same_name_from_another_remote_requires_New_Branch_without_rebinding()
    {
        using var repo = Fixture("origin", "upstream");
        repo.Git("branch", "--track", "feature/topic", "origin/feature/topic");
        var before = repo.Git("show-ref");
        var context = await RepositoryActions.ReadAsync(repo.Root);
        var target = context.Branches.Single(b => b.Name == "upstream/feature/topic");
        Assert.NotNull(BranchCheckout.Resolve(context, target).Error);
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(context, BranchAction.Switch, target));
        Assert.Equal(before, repo.Git("show-ref"));
        Assert.Equal("origin", repo.Git("config", "branch.feature/topic.remote").Trim());
        Assert.Equal("feature/topic-2", BranchCheckout.SuggestName(context, target));
        await new BranchWriter().ExecuteAsync(context, BranchAction.CreateAndSwitch, target, "upstream-topic");
        Assert.Equal("upstream-topic", repo.Git("branch", "--show-current").Trim());
        Assert.Equal("upstream", repo.Git("config", "branch.upstream-topic.remote").Trim());
        Assert.Equal("origin", repo.Git("config", "branch.feature/topic.remote").Trim());
    }

    [Fact]
    public async Task Create_only_on_dirty_tree_preserves_branch_index_and_working_files()
    {
        using var repo = Fixture("origin"); repo.Write("a", "staged\n"); repo.Git("add", "a");
        repo.Write("a", "unstaged\n"); repo.Write("untracked", "keep\n");
        var context = await RepositoryActions.ReadAsync(repo.Root); var target = context.Branches.Single(b => b.Remote);
        var status = repo.Git("status", "--porcelain"); var index = repo.Git("write-tree"); var head = repo.Git("rev-parse", "HEAD");
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(context, BranchAction.CreateAndSwitch, target, "checkout-denied"));
        Assert.Throws<IOException>(() => repo.Git("rev-parse", "--verify", "refs/heads/checkout-denied"));
        await new BranchWriter().ExecuteAsync(context, BranchAction.Create, target, "create-only");
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("write-tree"));
        Assert.Equal(status, repo.Git("status", "--porcelain"));
        Assert.Equal("unstaged\n", File.ReadAllText(Path.Combine(repo.Root, "a")));
        Assert.Equal("origin", repo.Git("config", "branch.create-only.remote").Trim());
    }

    [Theory]
    [InlineData("@{-1}")]
    [InlineData("HEAD")]
    [InlineData("@")]
    [InlineData("-option")]
    [InlineData("bad..name")]
    [InlineData("bad name")]
    [InlineData("main")]
    public async Task Invalid_or_duplicate_names_leave_refs_and_files_unchanged(string name)
    {
        using var repo = Fixture("origin"); var before = repo.Git("show-ref");
        var context = await RepositoryActions.ReadAsync(repo.Root); var target = context.Branches.Single(b => b.Remote);
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(context, BranchAction.CreateAndSwitch, target, name));
        Assert.Equal(before, repo.Git("show-ref"));
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim()); Assert.Empty(repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Other_worktree_occupancy_is_explained_and_Git_still_enforces_it()
    {
        using var repo = Fixture("origin"); repo.Git("branch", "--track", "my-topic", "origin/feature/topic");
        var linked = Path.Combine(repo.Container, "linked"); repo.Git("worktree", "add", linked, "my-topic");
        var context = await RepositoryActions.ReadAsync(repo.Root); var target = context.Branches.Single(b => b.Remote);
        var worktrees = await WorktreeCatalog.ReadAsync(repo.Root);
        var reason = BranchActionAvailability.DisabledReason(context, BranchAction.Switch, target, worktrees.Entries);
        Assert.NotNull(reason); Assert.Contains("linked", reason);
        var index = repo.Git("write-tree"); var refs = repo.Git("show-ref"); var linkedFile = File.ReadAllBytes(Path.Combine(linked, "a"));
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(context, BranchAction.Switch, target));
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
        Assert.Equal(index, repo.Git("write-tree")); Assert.Equal(refs, repo.Git("show-ref"));
        Assert.Equal(linkedFile, File.ReadAllBytes(Path.Combine(linked, "a")));
    }

    [Fact]
    public async Task Stale_remote_snapshot_never_creates_a_branch_at_a_new_target()
    {
        using var repo = Fixture("origin"); var context = await RepositoryActions.ReadAsync(repo.Root);
        var target = context.Branches.Single(b => b.Remote);
        repo.Write("a", "advanced\n"); repo.Commit("advance"); repo.Git("update-ref", target.CanonicalName, "HEAD");
        var refs = repo.Git("show-ref");
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(context, BranchAction.CreateAndSwitch, target, "stale"));
        Assert.Equal(refs, repo.Git("show-ref")); Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
    }

    [Fact]
    public async Task Multiple_tracking_branches_are_not_chosen_arbitrarily()
    {
        using var repo = Fixture("origin");
        repo.Git("branch", "--track", "first", "origin/feature/topic"); repo.Git("branch", "--track", "second", "origin/feature/topic");
        var context = await RepositoryActions.ReadAsync(repo.Root); var target = context.Branches.Single(b => b.Remote);
        Assert.NotNull(BranchCheckout.Resolve(context, target).Error);
        await Assert.ThrowsAsync<IOException>(() => new BranchWriter().ExecuteAsync(context, BranchAction.Switch, target));
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
        repo.Git("switch", "second"); context = await RepositoryActions.ReadAsync(repo.Root); target = context.Branches.Single(b => b.Remote);
        Assert.Equal("second", BranchCheckout.Resolve(context, target).LocalName);
    }
}
