using System.Net;
using System.Text.Json;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ContinuousDiffTests
{
    [Fact]
    public async Task Local_scope_is_lazy_read_only_and_retains_a_return_to_the_previous_source()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "one\ntwo\n"); repo.Write("b.cs", "delete\n"); repo.Commit("base");
        repo.Write("a.cs", "one\nchanged\n"); File.Delete(Path.Combine(repo.Root, "b.cs")); repo.Write("新增🙂.cs", "new without newline");
        var head = repo.Git("rev-parse", "HEAD"); var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")); var status = repo.Git("status", "--porcelain");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile));
        model.RecordReadingPosition(new(model.DocumentVersion, 1, 2, 3)); model.SwitchView(1);
        Assert.True(await model.OpenContinuousDiffAsync()); var session = model.ContinuousDiff!;
        Assert.Null(model.CurrentDocument); Assert.Null(model.ActiveSourceTabId); Assert.False(model.CanStageFile); Assert.False(model.CanUnstageFile);
        Assert.Single(model.SourceTabs); Assert.True(model.CanReadBack); Assert.Equal(3, session.Manifest.Files.Count);
        foreach (var item in session.Manifest.Files)
        {
            var file = await session.ReadAsync(item.Index); Assert.NotNull(file);
            Assert.Equal(session.Manifest.Id, file.Id); Assert.NotEmpty(file.Rows);
            if (item.Path == "b.cs") { Assert.False(file.New!.Exists); Assert.All(file.Rows, row => Assert.Null(row.NewLine)); }
            if (item.Path == "新增🙂.cs") { Assert.False(file.Old!.Exists); Assert.False(file.New!.EndsWithNewline); }
        }
        Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion)); Assert.Null(model.ContinuousDiff);
        Assert.Equal("a.cs", model.CurrentDocument!.Path); Assert.Equal(2, model.CurrentDocument.RevealLine); Assert.Equal(3, model.CurrentDocument.RevealColumn);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")));
        Assert.Equal(status, repo.Git("status", "--porcelain")); Assert.Equal("one\nchanged\n", File.ReadAllText(Path.Combine(repo.Root, "a.cs")));
    }

    [Fact]
    public async Task Opening_a_captured_file_preserves_its_comparison_and_selected_side()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "one\ntwo\n"); repo.Commit("base"); repo.Write("a.cs", "one\nchanged\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("a.cs", "local", ComparisonKind.LocalChanges));
        model.RecordReadingPosition(new(model.DocumentVersion, 1, 2, 3)); await model.OpenContinuousDiffAsync();
        var session = model.ContinuousDiff!; var version = model.DocumentVersion;
        Assert.False(await model.OpenContinuousDiffFileAsync("stale", 0, 0, 2, version));
        Assert.True(await model.OpenContinuousDiffFileAsync(session.Manifest.Id, 0, 0, 2, version));
        Assert.Null(model.ContinuousDiff); Assert.True(model.CurrentDocument!.IsDiff); Assert.Equal(0, model.CurrentDocument.RevealSide);
        Assert.Equal(2, model.CurrentDocument.RevealLine); Assert.Null(model.CurrentDocument.Editing); Assert.Single(model.SourceTabs);
        Assert.Null(await session.ReadAsync(0));
        Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion));
        Assert.Equal(1, model.CurrentDocument!.RevealSide); Assert.Equal(2, model.CurrentDocument.RevealLine); Assert.Equal(3, model.CurrentDocument.RevealColumn);
    }

    [Fact]
    public async Task External_index_changes_are_rejected_even_before_a_watcher_refresh()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "old\n"); repo.Commit("base"); repo.Write("a.cs", "updated\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(1); await model.OpenContinuousDiffAsync();
        var session = model.ContinuousDiff!; repo.Git("add", "a.cs");
        var error = await Assert.ThrowsAsync<IOException>(async () => await session.ReadAsync(0));
        Assert.Contains("索引、HEAD", error.Message); Assert.NotEmpty(repo.Git("diff", "--cached"));
    }

    [Fact]
    public async Task File_pages_cover_the_complete_scope_without_eager_document_reads()
    {
        var reader = new ControlledReader(450); using var model = new MainViewModel(reader); await model.OpenAsync("repo"); model.SwitchView(1);
        Assert.True(await model.OpenContinuousDiffAsync()); var first = model.ContinuousDiff!;
        Assert.Empty(reader.Reads); Assert.Equal(450, first.Manifest.TotalFiles); Assert.Equal(200, first.Manifest.Files.Count);
        Assert.True(await model.OpenContinuousDiffAsync(200)); Assert.Null(await first.ReadAsync(0));
        Assert.Equal("file200.cs", model.ContinuousDiff!.Manifest.Files[0].Path); Assert.Equal(200, model.ContinuousDiff.Manifest.Offset);
        Assert.True(await model.OpenContinuousDiffAsync(400)); Assert.Equal(50, model.ContinuousDiff!.Manifest.Files.Count);
        Assert.False(await model.OpenContinuousDiffAsync(450)); Assert.False(await model.OpenContinuousDiffAsync(-1)); Assert.Empty(reader.Reads);
    }

    [Theory]
    [InlineData("refresh")] [InlineData("repository")] [InlineData("file")] [InlineData("dispose")]
    public async Task Obsolete_sessions_cannot_publish_a_late_file(string change)
    {
        var reader = new ControlledReader(2); using var model = new MainViewModel(reader); await model.OpenAsync("repo"); model.SwitchView(1); await model.OpenContinuousDiffAsync();
        var session = model.ContinuousDiff!; reader.Delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = reader.Delay; var read = session.ReadAsync(0); reader.Delay = null;
        if (change == "refresh") await model.RefreshAsync();
        else if (change == "repository") await model.OpenAsync("other");
        else if (change == "file") await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        else model.Dispose();
        pending.SetResult(new("file0.cs", "late", "plaintext", "", ""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        Assert.Null(await session.ReadAsync(0)); Assert.Null(model.ContinuousDiff);
    }

    [Fact]
    public async Task At_most_two_reads_run_and_errors_release_capacity_for_retry()
    {
        var reader = new ControlledReader(3); var current = true;
        using var session = Session(reader, () => current); reader.Delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = reader.Delay; var a = session.ReadAsync(0); var b = session.ReadAsync(1);
        await Assert.ThrowsAsync<ContinuousDiffBusyException>(async () => await session.ReadAsync(2)); Assert.Equal(2, reader.Reads.Count);
        pending.SetException(new IOException("retry")); await Assert.ThrowsAsync<IOException>(async () => await a); await Assert.ThrowsAsync<IOException>(async () => await b);
        reader.Delay = null; Assert.NotNull(await session.ReadAsync(2));
        current = false; Assert.Null(await session.ReadAsync(0)); Assert.Null(await session.ReadAsync(-1)); Assert.Null(await session.ReadAsync(3));
    }

    [Fact]
    public async Task HTTP_reads_are_bound_to_the_published_session_and_never_accept_a_path()
    {
        var reader = new ControlledReader(3); using var session = Session(reader, () => true); using var server = new EditorServer(); using var http = new HttpClient();
        var toolbar = new EditorToolbarState("", false, "", ContinuousDiff: session.Manifest);
        server.Publish(server.PreparePublication(7, new("", "continuous", "plaintext", "", "", IsNotice: true), toolbar, continuous: session));
        string Query(string id, int index, int version) => $"continuous-diff?id={id}&version={version}&index={index}";
        foreach (var query in new[] { Query("wrong", 0, 7), Query(session.Manifest.Id, 3, 7), Query(session.Manifest.Id, 0, 6), "continuous-diff?path=../../secret" })
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(server.Address, query))).StatusCode);
        Assert.Empty(reader.Reads);
        using var valid = JsonDocument.Parse(await http.GetStringAsync(new Uri(server.Address, Query(session.Manifest.Id, 1, 7))));
        Assert.Equal(1, valid.RootElement.GetProperty("index").GetInt32()); Assert.Equal("file1.cs", Assert.Single(reader.Reads).Path);
        reader.Delay = new(TaskCreationOptions.RunContinuationsAsynchronously); var pending = reader.Delay;
        var late = http.GetAsync(new Uri(server.Address, Query(session.Manifest.Id, 0, 7)));
        await Wait(() => reader.Reads.Count == 2);
        var another = http.GetAsync(new Uri(server.Address, Query(session.Manifest.Id, 2, 7)));
        await Wait(() => reader.Reads.Count == 3);
        using var busy = await http.GetAsync(new Uri(server.Address, Query(session.Manifest.Id, 1, 7)));
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode); Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter!.Delta);
        server.Publish(EditorServer.Serialize(8, new("a", "next", "plaintext", "", "")));
        pending.SetResult(new("file0.cs", "late", "plaintext", "", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await late).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await another).StatusCode);
    }

    [Fact]
    public async Task Fixed_revision_range_and_rename_requests_are_captured_exactly()
    {
        var reader = new ControlledReader(1); var old = new string('a', 40); var revision = new string('b', 40);
        using var session = new ContinuousDiffSession("root", 9, "range", [new("新.cs", "", ComparisonKind.RevisionRange, "旧.cs", revision, BaseCommit: old)], DiffWhitespace.Ignore, reader, () => true, default);
        await session.ReadAsync(0); var request = Assert.Single(reader.Reads);
        Assert.Equal("root", request.Root); Assert.Equal("新.cs", request.Path); Assert.Equal("旧.cs", request.OldPath);
        Assert.Equal(old, request.BaseCommit); Assert.Equal(revision, request.Commit); Assert.Equal(DiffWhitespace.Ignore, request.Whitespace);
    }

    [Fact]
    public async Task Projection_keeps_true_rows_context_formats_and_a_visible_large_file_limit()
    {
        var oldText = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"old {i}")) + "\r\n";
        var newText = oldText.Replace("old 20", "changed 20", StringComparison.Ordinal);
        var compared = await NativeDiff.CompareAsync(new("old.cs", "old", oldText, true), new("new.cs", "new", newText, true), default);
        var file = ContinuousDiffSession.Project("id", 0, new("new.cs", "", "plaintext", "", "", IsDiff: true, Diff: compared.Diff));
        Assert.Contains(file.Rows, row => row.OldLine == 20 && row.NewLine == 20 && row.Kind == "modified" && row.NewText == "changed 20");
        Assert.Contains(file.Rows, row => row.Kind == "gap"); Assert.True(file.Rows.Count < 25); Assert.Equal("混合 LF/CRLF", file.New!.Format);
        var text = string.Join('\n', Enumerable.Repeat("new", 7000));
        var large = await NativeDiff.CompareAsync(new("big", "old", "", false), new("big", "new", text, true), default);
        var projected = ContinuousDiffSession.Project("id", 0, new("big", "", "plaintext", "", "", IsDiff: true, Diff: large.Diff));
        Assert.Equal(6000, projected.Rows.Count); Assert.True(projected.Truncated); Assert.Equal(6000, projected.Rows[^1].NewLine);
        Assert.Equal("<script>safe text</script>", ContinuousDiffSession.Project("id", 0, new("a", "<script>safe text</script>", "", "", "", IsNotice: true)).Notice);
    }

    private static ContinuousDiffSession Session(ControlledReader reader, Func<bool> current) => new("repo", 7, "local",
        Enumerable.Range(0, 3).Select(i => new NavigationEntry($"file{i}.cs", "", ComparisonKind.LocalChanges)).ToArray(), DiffWhitespace.Trim, reader, current, default);
    private static async Task Wait(Func<bool> ready)
    { for (var i = 0; i < 100 && !ready(); i++) await Task.Delay(10); Assert.True(ready()); }
    private sealed class ControlledReader(int count) : IRepositoryReader
    {
        public System.Collections.Concurrent.ConcurrentQueue<DocumentRequest> Reads { get; } = new();
        public TaskCompletionSource<RepositoryDocument>? Delay { get; set; }
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", null,
            [new("b.cs")], Enumerable.Range(0, count).Select(i => new ChangeEntry($"file{i}.cs", ComparisonKind.Worktree, "Modified")).ToArray(), false));
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default)
        {
            Reads.Enqueue(request); return Delay?.Task ?? Task.FromResult(new RepositoryDocument(request.Path, "notice", "plaintext", "", "", IsNotice: true));
        }
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage([], false));
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
    }
}
