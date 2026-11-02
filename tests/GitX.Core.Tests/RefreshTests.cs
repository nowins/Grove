using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RefreshTests
{
    [Fact]
    public async Task Refresh_keeps_selection_and_does_not_republish_unchanged_document()
    {
        using var repo = new TestRepository();
        repo.Write("a.txt", "one\ntwo\n"); repo.Commit("base");
        using var model = new MainViewModel();
        await model.OpenAsync(repo.Root);
        var documents = new List<RepositoryDocument>();
        model.DocumentChanged += (_, doc) => documents.Add(doc);
        model.SelectedEntry = model.Entries.Single();
        await Until(() => documents.Any(d => d.Text == "one\ntwo\n"));
        var count = documents.Count; var entries = model.Entries;
        await model.RefreshAsync();
        Assert.Equal(count, documents.Count); Assert.Same(entries, model.Entries);
        Assert.Equal("a.txt", model.SelectedEntry?.Path);
        repo.Write("a.txt", "one\nchanged\n"); repo.Write("b.txt", "new\n");
        await model.RefreshAsync();
        Assert.Equal("a.txt", model.SelectedEntry?.Path);
        Assert.Equal("one\nchanged\n", documents.Last().Text);
        Assert.True(documents.Last().PreserveView);
        Assert.Contains(model.Entries, e => e.Path == "b.txt");
    }

    [Fact]
    public async Task Watcher_coalesces_worktree_events_and_observes_index_changes()
    {
        using var repo = new TestRepository();
        repo.Write("a.txt", "base"); repo.Commit("base");
        var count = 0;
        using var watcher = new RepositoryWatcher(repo.Root, () => Interlocked.Increment(ref count));
        for (var i = 0; i < 20; i++) repo.Write("a.txt", $"change {i}");
        await Until(() => Volatile.Read(ref count) > 0);
        await Task.Delay(500);
        Assert.InRange(count, 1, 3);
        var previous = count;
        repo.Git("add", "a.txt");
        await Until(() => Volatile.Read(ref count) > previous);
        watcher.Dispose(); previous = count;
        repo.Write("a.txt", "after close"); await Task.Delay(500);
        Assert.Equal(previous, count);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task Watcher_suppresses_ignored_output_but_keeps_tracked_paths_renames_and_ignore_changes()
    {
        using var repo = new TestRepository();
        repo.Write("build/tracked", "base"); repo.Write("visible", "base"); repo.Commit("base");
        repo.Write(".gitignore", "build/\n"); repo.Commit("ignore");
        var count = 0;
        using var watcher = new RepositoryWatcher(repo.Root, () => Interlocked.Increment(ref count));
        for (var i = 0; i < 20; i++) repo.Write("build/generated", $"output {i}");
        await Task.Delay(1000); Assert.Equal(0, count);
        repo.Write("build/tracked", "changed"); await Until(() => Volatile.Read(ref count) > 0);
        await Task.Delay(500); var previous = count;
        File.Move(Path.Combine(repo.Root, "visible"), Path.Combine(repo.Root, "build", "hidden"));
        await Until(() => Volatile.Read(ref count) > previous);
        await Task.Delay(500); previous = count;
        repo.Write(".gitignore", ""); await Until(() => Volatile.Read(ref count) > previous);
        watcher.Dispose(); previous = count;
        repo.Write("build/generated", "after dispose"); await Task.Delay(600); Assert.Equal(previous, count);
    }

    [Fact]
    public async Task Large_collection_reset_and_filtering_preserve_the_open_editor_document()
    {
        using var model = new MainViewModel(new ManyEntries());
        await model.OpenAsync("fixture"); model.SelectedEntry = model.Entries[1000];
        var selection = model.SelectedEntry;
        model.Filter = "file-01";
        Assert.Contains(selection!, model.Entries); Assert.Equal(selection, model.SelectedEntry);
        model.Filter = ""; Assert.Equal(20000, model.Entries.Count); Assert.Equal(selection, model.SelectedEntry);
        model.Filter = "missing"; Assert.Empty(model.Entries); Assert.Equal(selection, model.SelectedEntry);
        model.Filter = ""; Assert.Equal(20000, model.Entries.Count); Assert.Equal(selection, model.SelectedEntry);
        model.SelectedEntry = model.Entries[19999]; await model.RefreshAsync();
        Assert.Equal("file-19999.cs", model.SelectedEntry?.Path);
    }

    private sealed class ManyEntries : IRepositoryReader
    {
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", null,
            Enumerable.Range(0, 20000).Select(i => new FileEntry($"file-{i:D5}.cs")).ToArray(), [], false));
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage([], false));
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default) => Task.FromResult(new RepositoryDocument(request.Path, request.Path, "plaintext", "", ""));
    }
}
