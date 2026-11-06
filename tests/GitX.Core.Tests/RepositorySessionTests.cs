using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RepositorySessionTests
{
    [Fact]
    public async Task Session_merges_paths_and_keeps_events_arriving_during_a_read()
    {
        var reader = new ControlledReader();
        var session = new RepositorySession(reader, Snapshot("repo"));
        session.Invalidate(RepositoryChangeSet.Content(["a", "b"]));
        session.Invalidate(RepositoryChangeSet.Content(["a", "c"]));
        var first = session.RefreshAsync();
        Assert.Equal(new[] { "a", "b", "c" }, reader.Reads[0].Changes.Paths);
        session.Invalidate(RepositoryChangeSet.Content(["later"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RefreshAsync());
        reader.Complete(0); await first;
        Assert.True(session.HasPending);
        var next = session.RefreshAsync();
        Assert.Equal("later", Assert.Single(reader.Reads[1].Changes.Paths));
        reader.Complete(1); await next;
        Assert.False(session.HasPending);
    }

    [Fact]
    public async Task Failure_wrong_repository_and_ignored_cancellation_cannot_install_a_snapshot()
    {
        foreach (var failure in new[] { "exception", "wrong-root", "cancelled" })
        {
            var reader = new ControlledReader(); var original = Snapshot("repo");
            var session = new RepositorySession(reader, original);
            using var cancellation = new CancellationTokenSource();
            session.Invalidate(RepositoryChangeSet.Content(["a"]));
            var read = session.RefreshAsync(cancellation.Token);
            if (failure == "exception") reader.Reads[0].Done.SetException(new IOException("failed"));
            else
            {
                if (failure == "cancelled") cancellation.Cancel();
                reader.Reads[0].Done.SetResult(new(Snapshot(failure == "wrong-root" ? "other" : "repo"), RepositoryChangeSet.Full));
            }
            if (failure == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            else await Assert.ThrowsAsync<IOException>(() => read);
            Assert.Same(original, session.Snapshot);
            session.Invalidate(RepositoryChangeSet.Content(["b"]));
            var retry = session.RefreshAsync();
            Assert.True(reader.Reads[1].Changes.IsFull);
            reader.Complete(1); await retry;
        }
    }

    [Fact]
    public void Invalid_metadata_or_large_batches_require_full_reconciliation()
    {
        foreach (var path in new[] { "", "../a", "/root", "a\\b", ".git/index", "x/.gitignore", ".gitattributes", ".mailmap" })
            Assert.True(RepositoryChangeSet.Content([path]).IsFull);
        Assert.True(RepositoryChangeSet.Content(Enumerable.Range(0, 65).Select(i => $"file-{i}")).IsFull);
        Assert.True(RepositoryChangeSet.Content(["a"]).Merge(RepositoryChangeSet.Full).IsFull);
        Assert.True(RepositoryChangeSet.Content(["folder"]).Touches("folder/a"));
        Assert.False(RepositoryChangeSet.Content(["folder"]).Touches("folder-two/a"));
        var paths = new[] { "a" }; var change = RepositoryChangeSet.Content(paths); paths[0] = "b";
        Assert.Equal("a", Assert.Single(change.Paths));
    }

    [Fact]
    public async Task Unrelated_content_does_not_reread_the_document_or_rebuild_navigation()
    {
        var reader = new ControlledReader(); using var model = new MainViewModel(reader);
        await model.OpenAsync("repo");
        await model.OpenEditorEntryAsync(new("a", "", ComparisonKind.LocalChanges));
        var document = model.CurrentDocument; var version = model.DocumentVersion;
        var navigation = 0; model.NavigationChanged += () => navigation++;
        var unrelated = model.RefreshAsync(RepositoryChangeSet.Content(["b"])); reader.Complete(0); await unrelated;
        Assert.Equal(1, reader.DocumentReads); Assert.Same(document, model.CurrentDocument);
        Assert.Equal(version, model.DocumentVersion); Assert.Equal(0, navigation);
        var related = model.RefreshAsync(RepositoryChangeSet.Content(["a"])); reader.Complete(1); await related;
        Assert.Equal(2, reader.DocumentReads);
        var manual = model.RefreshAsync(); reader.Complete(2); await manual;
        Assert.Equal(3, reader.DocumentReads); Assert.True(reader.Reads[2].Changes.IsFull);
    }

    [Fact]
    public async Task Historical_diff_ignores_worktree_content_but_full_refresh_still_rechecks_it()
    {
        var reader = new ControlledReader(); using var model = new MainViewModel(reader);
        await model.OpenAsync("repo");
        await model.OpenEditorEntryAsync(new("a", "", ComparisonKind.Commit, Commit: new string('a', 40)));
        var unrelated = model.RefreshAsync(RepositoryChangeSet.Content(["a"])); reader.Complete(0); await unrelated;
        Assert.Equal(1, reader.DocumentReads);
        var full = model.RefreshAsync(); reader.Complete(1); await full;
        Assert.Equal(2, reader.DocumentReads);
    }

    [Fact]
    public async Task Late_old_refresh_cannot_clear_new_refresh_ownership_and_root_changes_are_notified()
    {
        var reader = new ControlledReader(); using var model = new MainViewModel(reader);
        await model.OpenAsync("old");
        var roots = new List<string?>();
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Root)) roots.Add(model.Root); };
        var old = model.RefreshAsync(RepositoryChangeSet.Content(["a"]));
        await model.OpenAsync("new");
        Assert.Equal(new string?[] { null, "new" }, roots);
        var current = model.RefreshAsync(RepositoryChangeSet.Content(["b"]));
        reader.Complete(0); await old;
        var queued = model.RefreshAsync(RepositoryChangeSet.Content(["c"]));
        Assert.Same(current, queued); Assert.Equal(2, reader.Reads.Count);
        reader.Complete(1);
        await Until(() => reader.Reads.Count == 3);
        Assert.Equal("new", reader.Reads[2].Snapshot.Root);
        Assert.Equal("c", Assert.Single(reader.Reads[2].Changes.Paths));
        reader.Complete(2); await current;
        Assert.Equal("new", model.Root);
        reader.FailOpen = true;
        await model.OpenAsync("broken");
        Assert.Null(model.Root); Assert.Null(roots[^1]); Assert.True(model.HasError);
    }

    private static RepositorySnapshot Snapshot(string root) => new(root, "main", null, [new("a"), new("b")], [], false);
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10); Assert.True(condition()); }

    private sealed class ControlledReader : IRepositoryReader
    {
        internal sealed record Read(RepositorySnapshot Snapshot, RepositoryChangeSet Changes, TaskCompletionSource<RepositoryRefreshResult> Done);
        public List<Read> Reads { get; } = [];
        public int DocumentReads { get; private set; }
        public bool FailOpen { get; set; }
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => FailOpen
            ? Task.FromException<RepositorySnapshot>(new IOException("cannot open")) : Task.FromResult(Snapshot(path));
        public Task<RepositoryRefreshResult> RefreshAsync(RepositorySnapshot previous, RepositoryChangeSet changes, CancellationToken token = default)
        {
            var done = new TaskCompletionSource<RepositoryRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Reads.Add(new(previous, changes, done)); return done.Task;
        }
        public void Complete(int index) => Reads[index].Done.SetResult(new(Reads[index].Snapshot, Reads[index].Changes));
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage([], false));
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default)
        { DocumentReads++; return Task.FromResult(new RepositoryDocument(request.Path, "text", "plaintext", "", "")); }
    }
}
