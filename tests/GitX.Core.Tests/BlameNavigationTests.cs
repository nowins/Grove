using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class BlameNavigationTests
{
    [Fact]
    public async Task Blame_commands_reject_old_document_versions_and_close_preserves_view()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "first\n"); repo.Write("b.cs", "second\n"); repo.Commit("base");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        model.SelectedEntry = model.Entries.Single(e => e.Path == "a.cs");
        await model.ToggleBlameAsync();
        var version = model.DocumentVersion;
        Assert.NotNull(model.BlameLineAt(1, version)); Assert.True(model.CurrentDocument!.PreserveView);
        model.SelectedEntry = model.Entries.Single(e => e.Path == "b.cs");
        Assert.Null(model.BlameLineAt(1, version)); Assert.Null(model.BlameLineAt(1, model.DocumentVersion));
        await model.ToggleBlameAsync();
        Assert.Null(model.CurrentDocument!.Blame); Assert.True(model.CurrentDocument.PreserveView);
        await model.ToggleBlameAsync(); Assert.NotNull(model.BlameLineAt(1, model.DocumentVersion));
        Assert.Null(model.BlameLineAt(0, model.DocumentVersion)); Assert.Null(model.BlameLineAt(2, model.DocumentVersion));
    }

    [Fact]
    public async Task File_history_resets_unrelated_filters_without_changing_git_state()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "base\n"); repo.Commit("base");
        repo.Write("b.cs", "other\n"); repo.Commit("other file");
        repo.Write("a.cs", "changed\n"); repo.Commit("change");
        var head = repo.Git("rev-parse", "HEAD");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(2);
        await model.SelectHistoryReferenceAsync("refs/heads/main");
        model.HistoryAuthor = "not this author"; await model.ApplyHistoryQueryAsync(); Assert.Empty(model.Commits);
        await model.ShowFileHistoryAsync("a.cs");
        Assert.Null(model.HistoryReferenceKey); Assert.Equal("", model.HistoryAuthor); Assert.Equal("a.cs", model.HistoryPath);
        Assert.Equal(new[] { "change", "base" }, model.Commits.Select(c => c.Subject));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal("", repo.Git("status", "--porcelain"));
    }
}
