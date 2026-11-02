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
