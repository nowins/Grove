using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ShellNavigationTests
{
    [Fact]
    public async Task RepositoryChoiceResolvesNestedDirectoryBeforeSwitching()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitx-choice-" + Guid.NewGuid().ToString("N"));
        try
        {
            LibGit2Sharp.Repository.Init(directory);
            var nested = Path.Combine(directory, "nested"); Directory.CreateDirectory(nested);
            Assert.Equal(directory, await RepositoryReader.ResolveWorkingDirectoryAsync(nested));
            await Assert.ThrowsAsync<IOException>(() => RepositoryReader.ResolveWorkingDirectoryAsync(Path.Combine(directory, "missing")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RepositoryChoiceRejectsOrdinaryAndBareFolders()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitx-choice-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await Assert.ThrowsAsync<IOException>(() => RepositoryReader.ResolveWorkingDirectoryAsync(directory));
            LibGit2Sharp.Repository.Init(directory, true);
            await Assert.ThrowsAsync<IOException>(() => RepositoryReader.ResolveWorkingDirectoryAsync(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RecentProjectsRestoreCanonicalRootsInMostRecentlyOpenedOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitx-recents-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecentRepositoryStore(directory);
            var first = Path.Combine(directory, "project-one"); var second = Path.Combine(directory, "项目 二");
            Assert.Empty(await store.ReadAsync());
            await store.RememberAsync(first); await store.RememberAsync(second);
            await store.RememberAsync(first + Path.DirectorySeparatorChar);
            Assert.Equal(new[] { first, second }, await new RecentRepositoryStore(directory).ReadAsync());
            Assert.False(Directory.Exists(first)); // Reading the MRU does not scan or create repository directories.
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ConcurrentRecentUpdatesAreSerializedAndBounded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitx-recents-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecentRepositoryStore(directory);
            await Task.WhenAll(Enumerable.Range(0, 25).Select(i => store.RememberAsync(Path.Combine(directory, i.ToString()))));
            var restored = await new RecentRepositoryStore(directory).ReadAsync();
            Assert.Equal(16, restored.Count); Assert.Equal(16, restored.Distinct().Count());
            Assert.Equal("recent-repositories.json", Path.GetFileName(Assert.Single(Directory.GetFiles(directory))));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CorruptRecentListDoesNotPreventOpeningANewProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitx-recents-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "recent-repositories.json"), "[broken");
            var root = Path.Combine(directory, "repo"); var store = new RecentRepositoryStore(directory);
            Assert.Empty(await store.ReadAsync());
            Assert.Equal(new[] { root }, await store.RememberAsync(root));
            Assert.Equal(new[] { root }, await store.ReadAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RemoteCheckoutReusesItsTrackingLocalBranchAndCurrentState()
    {
        var current = new BranchEntry("main", "refs/heads/main", new('a',40), false, true, "origin/main");
        var remote = new BranchEntry("origin/main", "refs/remotes/origin/main", new('b',40), true, false, null);
        var context = new ActionContext("repo", "token", current.Sha, "main", "", false, false, [current, remote], []);
        Assert.Equal(current, BranchActionAvailability.CheckoutTarget(context, remote));
        Assert.NotNull(BranchActionAvailability.DisabledReason(context, BranchAction.Switch, remote));
        Assert.NotNull(BranchActionAvailability.DisabledReason(context, BranchAction.Delete, remote));
        Assert.Null(BranchActionAvailability.DisabledReason(context, BranchAction.Create, remote));
    }

    [Fact]
    public void DirtyAndOperationStatesExplainDisabledBranchWrites()
    {
        var target = new BranchEntry("topic", "refs/heads/topic", new('a',40), false, false, null);
        var context = new ActionContext("repo", "token", target.Sha, "main", "", true, false, [target], []);
        foreach (var action in new[] { BranchAction.Switch, BranchAction.Merge, BranchAction.Rebase })
            Assert.NotNull(BranchActionAvailability.DisabledReason(context, action, target));
        Assert.Null(BranchActionAvailability.DisabledReason(context, BranchAction.Create, target));
        Assert.Null(BranchActionAvailability.DisabledReason(context, BranchAction.Delete, target));
        foreach (var action in Enum.GetValues<BranchAction>())
            Assert.NotNull(BranchActionAvailability.DisabledReason(context with { InOperation = true }, action, target));
    }
}
