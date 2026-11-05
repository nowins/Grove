using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HunkRecoveryViewModelTests
{
    [Fact]
    public async Task Direct_revert_exposes_undo_and_restore_clears_notice()
    {
        using var repo = new TestRepository();
        repo.Write("a.txt", "before\n"); repo.Commit("base"); repo.Write("a.txt", "after\n");
        var head = repo.Git("rev-parse", "HEAD"); var index = repo.Git("write-tree");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.LocalChanges);
        var document = await new RepositoryReader().DocumentAsync(request);
        await model.RevertBlockAsync(request, document, 0);
        Assert.False(model.HasError); Assert.True(model.HasRecovery); Assert.True(model.CanRestoreDiscard);
        Assert.Contains("a.txt", model.RecoveryNotice);
        Assert.Equal("before\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        await model.RestoreDiscardAsync();
        Assert.False(model.HasError); Assert.False(model.HasRecovery); Assert.False(model.CanRestoreDiscard);
        Assert.Equal("", model.RecoveryNotice);
        Assert.Equal("after\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("write-tree"));
    }

    [Fact]
    public async Task Repeated_stale_click_and_external_edit_never_overwrite_newer_content()
    {
        using var repo = new TestRepository();
        repo.Write("a.txt", "before\n"); repo.Commit("base"); repo.Write("a.txt", "after\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.LocalChanges);
        var document = await new RepositoryReader().DocumentAsync(request);
        await model.RevertBlockAsync(request, document, 0);
        var recovery = model.RecoveryDescription;
        await model.RevertBlockAsync(request, document, 0);
        Assert.True(model.HasError); Assert.Equal(recovery, model.RecoveryDescription);
        repo.Write("a.txt", "external edit\n");
        await model.RestoreDiscardAsync();
        Assert.True(model.HasError); Assert.True(model.HasRecovery);
        Assert.Equal("external edit\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal(recovery, model.RecoveryDescription);
    }

    [Fact]
    public async Task Recovery_cannot_run_or_appear_in_a_different_repository()
    {
        using var repo = new TestRepository(); using var other = new TestRepository();
        repo.Write("a.txt", "before\n"); repo.Commit("base"); repo.Write("a.txt", "after\n");
        other.Write("a.txt", "other\n"); other.Commit("other");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.LocalChanges);
        await model.RevertBlockAsync(request, await new RepositoryReader().DocumentAsync(request), 0);
        await model.OpenAsync(other.Root);
        Assert.False(model.HasRecovery); Assert.False(model.CanRestoreDiscard);
        await model.RestoreDiscardAsync();
        Assert.Equal("other\n", File.ReadAllText(Path.Combine(other.Root, "a.txt")));
        Assert.Equal("before\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }
}
