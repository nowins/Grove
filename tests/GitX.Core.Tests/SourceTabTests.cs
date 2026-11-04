using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class SourceTabTests
{
    [Fact]
    public async Task Closing_background_tab_does_not_reload_active_and_closing_active_selects_a_neighbor()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "one\n"); repo.Write("b.txt", "two\n"); repo.Commit("base");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        model.SelectedEntry = model.Entries[0]; await UntilAsync(() => model.CurrentDocument is not null); var a = model.ActiveSourceTabId!;
        model.SelectedEntry = model.Entries[1]; await UntilAsync(() => model.CurrentDocument is not null); var b = model.ActiveSourceTabId!;
        var document = model.CurrentDocument; var version = model.DocumentVersion; var changes = 0; model.SourceTabsChanged += () => changes++;
        await model.CloseSourceTabAsync(a, version - 1); Assert.Equal(2, model.SourceTabs.Count);
        await model.CloseSourceTabAsync(a, version); Assert.Single(model.SourceTabs); Assert.Same(document, model.CurrentDocument); Assert.Equal(version, model.DocumentVersion); Assert.Equal(1, changes);
        model.SelectedEntry = model.Entries[0]; await UntilAsync(() => model.CurrentDocument is not null);
        await model.CloseSourceTabAsync(model.ActiveSourceTabId!, model.DocumentVersion); Assert.Equal(b, model.ActiveSourceTabId); Assert.Equal("two\n", model.CurrentDocument!.Text);
        await model.CloseSourceTabAsync(b, model.DocumentVersion); Assert.Empty(model.SourceTabs); Assert.Null(model.CurrentDocument); Assert.Null(model.SelectedEntry);
    }

    [Fact]
    public async Task Tabs_survive_mode_switches_and_filtering_but_not_opening_another_repository()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "one\n"); repo.Commit("first"); repo.Write("a.cs", "two\n"); repo.Commit("second");
        using var next = new TestRepository(); next.Write("other", "other\n"); next.Commit("other");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SelectedEntry = model.Entries[0]; await model.ToggleBlameAsync();
        await model.PreviousAnnotationAsync(1, model.DocumentVersion); var historyId = model.ActiveSourceTabId!; var revision = model.CurrentDocument!.SourceRevision;
        model.SwitchView(1); Assert.Equal(historyId, model.ActiveSourceTabId); Assert.Equal(2, model.SourceTabs.Count);
        model.SwitchView(0); await UntilAsync(() => model.CurrentDocument is not null); Assert.Equal(historyId, model.ActiveSourceTabId); Assert.Equal(revision, model.CurrentDocument!.SourceRevision);
        model.Filter = "hidden"; Assert.Equal(historyId, model.ActiveSourceTabId);
        await model.SelectSourceTabAsync(historyId, model.DocumentVersion); Assert.Equal(revision, model.CurrentDocument!.SourceRevision);
        await model.OpenAsync(next.Root); Assert.Empty(model.SourceTabs); Assert.Null(model.ActiveSourceTabId);
        await model.SelectSourceTabAsync(historyId, model.DocumentVersion); Assert.Null(model.CurrentDocument);
    }

    [Fact]
    public async Task Closing_or_switching_a_loading_tab_rejects_late_reads()
    {
        var reader = new PendingReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        model.SelectedEntry = model.Entries[0]; var a = model.ActiveSourceTabId!; var oldA = reader.Reads[^1];
        model.SelectedEntry = model.Entries[1]; var b = model.ActiveSourceTabId!; var oldB = reader.Reads[^1];
        var closing = model.CloseSourceTabAsync(b, model.DocumentVersion); var currentA = reader.Reads[^1];
        oldB.SetResult(Document("b.txt", "late b")); oldA.SetResult(Document("a.txt", "old a")); currentA.SetResult(Document("a.txt", "current a")); await closing;
        Assert.Equal(a, model.ActiveSourceTabId); Assert.Single(model.SourceTabs); Assert.Equal("current a", model.CurrentDocument!.Text);
        var version = model.DocumentVersion; await model.SelectSourceTabAsync(b, version); Assert.Equal(version, model.DocumentVersion);
        model.SelectedEntry = model.Entries[1]; var lastB = reader.Reads[^1];
        var switching = model.SelectSourceTabAsync(a, model.DocumentVersion); var latestA = reader.Reads[^1];
        latestA.SetResult(Document("a.txt", "latest a")); await switching;
        lastB.SetResult(Document("b.txt", "stale b")); await Task.Delay(30);
        Assert.Equal("latest a", model.CurrentDocument!.Text);
    }

    [Fact]
    public async Task Closing_others_keeps_the_active_document_and_view_publication_without_reloading()
    {
        using var repo = new TestRepository();
        foreach (var path in new[] { "a.txt", "目录/b.txt", "c.txt" }) repo.Write(path, path + "\n");
        repo.Commit("base"); using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        foreach (var path in new[] { "a.txt", "目录/b.txt", "c.txt" }) await model.OpenEditorEntryAsync(new(path, path, ComparisonKind.WorkingFile));
        var active = model.ActiveSourceTabId!; var document = model.CurrentDocument; var version = model.DocumentVersion;
        var publications = 0; var tabChanges = 0;
        model.DocumentChanged += (_, _) => publications++; model.SourceTabsChanged += () => tabChanges++;
        await model.CloseSourceTabsAsync(active, version, SourceTabCloseScope.Others);
        Assert.Equal(active, Assert.Single(model.SourceTabs).Id); Assert.Same(document, model.CurrentDocument);
        Assert.Equal(version, model.DocumentVersion); Assert.Equal(0, publications); Assert.Equal(1, tabChanges);
        await model.CloseSourceTabsAsync(active, version, SourceTabCloseScope.Others);
        Assert.Equal(1, tabChanges); Assert.Equal(0, publications);
    }

    [Fact]
    public async Task Closing_others_around_a_background_tab_selects_its_exact_revision_once_and_never_writes_Git()
    {
        using var repo = new TestRepository(); repo.Write("same.txt", "historical\n"); repo.Write("other.txt", "other\n"); repo.Commit("old");
        var revision = repo.Git("rev-parse", "HEAD").Trim(); repo.Write("same.txt", "current\n"); repo.Commit("current");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("same.txt", "same.txt", ComparisonKind.WorkingFile)); var working = model.ActiveSourceTabId!;
        await model.OpenRevisionSourceAsync("same.txt", revision); var historical = model.ActiveSourceTabId!;
        await model.OpenEditorEntryAsync(new("other.txt", "other.txt", ComparisonKind.WorkingFile));
        Assert.Equal(3, model.SourceTabs.Count); Assert.NotEqual(working, historical);
        var before = FixtureState(); var completed = 0;
        model.DocumentChanged += (_, document) => { if (!document.IsNotice) completed++; };
        await model.CloseSourceTabsAsync(historical, model.DocumentVersion, SourceTabCloseScope.Others);
        Assert.Equal(historical, Assert.Single(model.SourceTabs).Id); Assert.Equal(historical, model.ActiveSourceTabId);
        Assert.Equal(revision, model.CurrentDocument!.SourceRevision); Assert.Equal("historical\n", model.CurrentDocument.Text);
        Assert.Equal(1, completed); Assert.Equal(before, FixtureState());
        string FixtureState() => repo.Git("rev-parse", "HEAD") + repo.Git("symbolic-ref", "HEAD") + repo.Git("diff", "--cached", "--raw")
            + repo.Git("status", "--porcelain=v1", "--untracked-files=all") + repo.Git("hash-object", "same.txt", "other.txt");
    }

    [Fact]
    public async Task Batch_closing_rejects_stale_versions_missing_targets_and_invalid_scopes_including_after_repository_switch()
    {
        using var first = new TestRepository(); first.Write("same.txt", "first\n"); first.Commit("first");
        using var next = new TestRepository(); next.Write("same.txt", "next\n"); next.Commit("next");
        using var model = new MainViewModel(); await model.OpenAsync(first.Root);
        await model.OpenEditorEntryAsync(new("same.txt", "same.txt", ComparisonKind.WorkingFile));
        var original = model.ActiveSourceTabId!; var version = model.DocumentVersion;
        await model.CloseSourceTabsAsync(original, version - 1, SourceTabCloseScope.All);
        await model.CloseSourceTabsAsync("missing", version, SourceTabCloseScope.All);
        await model.CloseSourceTabsAsync(original, version, (SourceTabCloseScope)99);
        Assert.Equal(original, Assert.Single(model.SourceTabs).Id);
        await model.OpenAsync(next.Root); await model.OpenEditorEntryAsync(new("same.txt", "same.txt", ComparisonKind.WorkingFile));
        var current = model.ActiveSourceTabId!; version = model.DocumentVersion;
        await model.CloseSourceTabsAsync(original, version, SourceTabCloseScope.All);
        Assert.Equal(current, Assert.Single(model.SourceTabs).Id); Assert.Equal("next\n", model.CurrentDocument!.Text);
        await model.CloseSourceTabsAsync(current, version, SourceTabCloseScope.All);
        Assert.Empty(model.SourceTabs); Assert.Null(model.CurrentDocument); Assert.Null(model.SelectedEntry); Assert.Null(model.ActiveSourceTabId);
        var cleared = model.DocumentVersion;
        await model.CloseSourceTabsAsync(current, cleared, SourceTabCloseScope.All);
        Assert.Equal(cleared, model.DocumentVersion);
    }

    [Fact]
    public async Task Closing_all_tabs_cancels_pending_reads_and_late_completions_cannot_reopen_a_document()
    {
        var reader = new PendingReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        model.SelectedEntry = model.Entries[0]; var a = model.ActiveSourceTabId!; var oldA = reader.Reads[^1];
        model.SelectedEntry = model.Entries[1]; var oldB = reader.Reads[^1];
        Assert.NotNull(a); Assert.Equal(2, model.SourceTabs.Count); Assert.NotNull(model.ActiveSourceTabId);
        var beforeVersion = model.DocumentVersion;
        await model.CloseSourceTabsAsync(a, model.DocumentVersion, SourceTabCloseScope.All); var version = model.DocumentVersion;
        Assert.True(version > beforeVersion, $"Close All must invalidate the current read: {beforeVersion} -> {version}; tabs={model.SourceTabs.Count}, active={model.ActiveSourceTabId}");
        oldA.SetResult(Document("a.txt", "late a")); oldB.SetResult(Document("b.txt", "late b")); await Task.Delay(30);
        Assert.Empty(model.SourceTabs); Assert.Null(model.CurrentDocument); Assert.Null(model.ActiveSourceTabId); Assert.Equal(version, model.DocumentVersion);
    }

    [Fact]
    public async Task Closing_others_while_loading_keeps_only_one_current_read_for_the_target()
    {
        var reader = new PendingReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        model.SelectedEntry = model.Entries[0]; var a = model.ActiveSourceTabId!; var oldA = reader.Reads[^1];
        model.SelectedEntry = model.Entries[1]; var oldB = reader.Reads[^1];
        var closing = model.CloseSourceTabsAsync(a, model.DocumentVersion, SourceTabCloseScope.Others); var currentA = reader.Reads[^1];
        Assert.Equal(3, reader.Reads.Count);
        oldA.SetResult(Document("a.txt", "old a")); oldB.SetResult(Document("b.txt", "late b")); currentA.SetResult(Document("a.txt", "current a")); await closing;
        Assert.Equal(a, Assert.Single(model.SourceTabs).Id); Assert.Equal(a, model.ActiveSourceTabId); Assert.Equal("current a", model.CurrentDocument!.Text);
    }

    [Fact]
    public async Task Closing_all_tabs_keeps_the_current_tool_mode_and_clears_only_the_editor()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "source\n"); repo.Commit("base");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("a.txt", "a.txt", ComparisonKind.WorkingFile)); var tab = model.ActiveSourceTabId!;
        model.SwitchView(2); var version = model.DocumentVersion; var document = model.CurrentDocument;
        await model.CloseSourceTabsAsync(tab, version, SourceTabCloseScope.All);
        Assert.Empty(model.SourceTabs); Assert.Equal(2, model.Mode); Assert.True(model.DocumentVersion > version); Assert.Null(model.CurrentDocument); Assert.Null(model.SelectedEntry);
    }


    private static RepositoryDocument Document(string path, string text) => new(path, text, "plaintext", "working", "");
    private static async Task UntilAsync(Func<bool> ready) { for (var i = 0; i < 100 && !ready(); i++) await Task.Delay(20); Assert.True(ready()); }
    private sealed class PendingReader : IRepositoryReader
    {
        public List<TaskCompletionSource<RepositoryDocument>> Reads { get; } = [];
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", null, [new("a.txt"), new("b.txt")], [], false));
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default) { var read = new TaskCompletionSource<RepositoryDocument>(TaskCreationOptions.RunContinuationsAsynchronously); Reads.Add(read); return read.Task; }
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage([], false));
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
    }
}
