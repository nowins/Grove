using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class WorkspaceNavigationTests
{
    [Fact]
    public async Task Source_local_and_commit_diffs_share_tabs_with_independent_write_contexts()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "first\n"); repo.Commit("first");
        repo.Write("a.cs", "second\n"); repo.Commit("second"); repo.Write("a.cs", "local\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(model.NavigationEntries(0).Single()); var source = model.ActiveSourceTabId!;
        await model.OpenEditorEntryAsync(model.NavigationEntries(1).Single()); var local = model.ActiveSourceTabId!;
        Assert.Equal(ComparisonKind.LocalChanges, model.CurrentWrite()!.Value.Request.Kind);
        Assert.True(model.CurrentDocument!.CanInclude);
        var sha = model.Commits[0].Sha;
        await model.OpenEditorEntryAsync(new("a.cs", "", ComparisonKind.Commit, Commit: sha)); var commit = model.ActiveSourceTabId!;
        Assert.Equal(3, model.SourceTabs.Count); Assert.Null(model.CurrentWrite()); Assert.False(model.CanBlame);
        Assert.Equal("second\n", model.CurrentDocument!.Diff!.New.Text);
        await model.SelectSourceTabAsync(source, model.DocumentVersion);
        Assert.Equal("local\n", model.CurrentDocument!.Text); Assert.True(model.CanBlame); Assert.Null(model.CurrentWrite());
        await model.SelectSourceTabAsync(local, model.DocumentVersion);
        Assert.Equal("local\n", model.CurrentDocument!.Diff!.New.Text); Assert.Equal(ComparisonKind.LocalChanges, model.CurrentWrite()!.Value.Request.Kind);
        await model.SelectSourceTabAsync(commit, model.DocumentVersion);
        Assert.Equal("second\n", model.CurrentDocument!.Diff!.New.Text); Assert.Null(model.CurrentWrite());
    }

    [Fact]
    public async Task Log_selection_and_queries_do_not_replace_or_close_the_reading_tab()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "a\n"); repo.Commit("first");
        repo.Write("b.txt", "b\n"); repo.Commit("second");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(model.NavigationEntries(0).First(entry => entry.Path == "a.txt"));
        var version = model.DocumentVersion; var document = model.CurrentDocument; var tab = model.ActiveSourceTabId;
        model.SelectedCommit = model.Commits[0]; await Until(() => model.HistoryEntries.Count > 0);
        Assert.Equal("b.txt", Assert.Single(model.HistoryEntries).Path);
        model.HistoryText = "first"; await model.ApplyHistoryQueryAsync();
        await Until(() => model.HistoryEntries.Count > 0);
        Assert.Equal("a.txt", Assert.Single(model.HistoryEntries).Path);
        Assert.Same(document, model.CurrentDocument); Assert.Equal(version, model.DocumentVersion); Assert.Equal(tab, model.ActiveSourceTabId);
        Assert.Equal(0, model.Mode); Assert.Equal(2, model.NavigationEntries(0).Count);
    }

    [Fact]
    public async Task Commit_inclusion_is_independent_of_the_active_source_tab()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "first\n"); repo.Write("b.txt", "base\n"); repo.Commit("base");
        repo.Write("a.txt", "selected\n"); repo.Write("b.txt", "excluded\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(model.NavigationEntries(0).First(entry => entry.Path == "b.txt"));
        model.SetFilesIncluded(["a.txt"], true); model.CommitMessage = "selected only";
        Assert.True(model.CanCommit); await model.CommitAsync(); Assert.False(model.HasError, model.Error);
        Assert.Equal("selected", repo.Git("show", "HEAD:a.txt").Trim());
        Assert.Equal("base", repo.Git("show", "HEAD:b.txt").Trim());
        Assert.Contains("b.txt", repo.Git("diff", "--name-only"));
        Assert.Equal("b.txt", model.CurrentDocument!.Path); Assert.Equal("excluded\n", model.CurrentDocument.Text);
    }

    [Fact]
    public async Task Same_path_different_comparisons_have_distinct_tabs_and_repo_change_clears_them()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "one\n"); repo.Commit("one");
        repo.Write("a.txt", "two\n"); repo.Commit("two");
        using var other = new TestRepository(); other.Write("other", "other"); other.Commit("other");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("a.txt", "", ComparisonKind.Commit, Commit: model.Commits[0].Sha)); var first = model.ActiveSourceTabId;
        await model.OpenEditorEntryAsync(new("a.txt", "", ComparisonKind.Commit, Commit: model.Commits[1].Sha));
        Assert.Equal(2, model.SourceTabs.Count); Assert.NotEqual(first, model.ActiveSourceTabId);
        model.SelectedCommit = model.Commits[0]; await Until(() => model.HistoryEntries.Count > 0);
        await model.OpenAsync(other.Root); Assert.Empty(model.SourceTabs); Assert.Empty(model.HistoryEntries); Assert.Null(model.CurrentDocument);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Search_and_notes_open_source_from_a_diff_without_closing_its_tab(bool note)
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "recorded\n"); repo.Commit("base");
        using var reader = new RepositoryReader();
        var anchor = CommentAnchor.Capture(await reader.DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.WorkingFile)), 1, 1, 1);
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("a.txt", "", ComparisonKind.Commit, Commit: model.Commits[0].Sha));
        var diff = model.ActiveSourceTabId;
        Assert.True(note ? await model.OpenNoteSourceAsync(repo.Root, anchor) : await model.OpenCodeSearchMatchAsync(repo.Root, new("a.txt", 1, 1, 8, "recorded")));
        Assert.Equal(2, model.SourceTabs.Count); Assert.Equal(0, model.Mode); Assert.Equal("recorded\n", model.CurrentDocument!.Text);
        await model.SelectSourceTabAsync(diff!, model.DocumentVersion); Assert.Equal(2, model.Mode); Assert.True(model.CurrentDocument!.IsDiff);
    }

    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 150 && !condition(); ++i) await Task.Delay(20); Assert.True(condition()); }
}
