using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

// Controlled completion order verifies the UI state contract even when a native read ignores cancellation.
public sealed class NavigationRaceTests
{
    [Fact]
    public async Task Automatic_paging_serializes_requests_and_preserves_selection_files_and_document()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo")); await opening;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = model.LoadHistoryAsync();
        var selected = new CommitEntry("first", "first", "author", DateTimeOffset.Now);
        reader.History.SetResult(new([selected], true)); await first;
        model.SelectedCommit = selected; reader.CommitChanges.SetResult([new("a.txt", ComparisonKind.Commit, "Modified")]);
        await UntilAsync(() => model.HistoryEntries.Count == 1);
        var files = model.HistoryEntries; var documentVersion = model.DocumentVersion;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = model.LoadHistoryAsync(automatic: true); var calls = reader.HistoryCalls.Count;
        Assert.True(model.HistoryLoading); Assert.False(model.CanAutoLoadHistory);
        await model.LoadHistoryAsync(automatic: true); Assert.Equal(calls, reader.HistoryCalls.Count);
        reader.History.SetResult(new([new("second", "second", "author", DateTimeOffset.Now)], false)); await next;
        Assert.Equal(new[] { "first", "second" }, model.Commits.Select(c => c.Sha));
        Assert.Same(selected, model.SelectedCommit); Assert.Same(files, model.HistoryEntries); Assert.Equal(documentVersion, model.DocumentVersion);
        Assert.False(model.HistoryLoading); Assert.False(model.CanAutoLoadHistory);
        await model.LoadHistoryAsync(automatic: true); Assert.Equal(calls, reader.HistoryCalls.Count);
    }

    [Fact]
    public async Task Failed_page_pauses_automatic_loading_until_explicit_retry_at_the_same_offset()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo")); await opening;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = model.LoadHistoryAsync(); reader.History.SetResult(new([new("first", "first", "author", DateTimeOffset.Now)], true)); await first;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = model.LoadHistoryAsync(automatic: true); reader.History.SetException(new IOException("read failed")); await failed;
        Assert.True(model.HistoryLoadFailed); Assert.Single(model.Commits); Assert.False(model.CanAutoLoadHistory);
        var calls = reader.HistoryCalls.Count;
        await model.LoadHistoryAsync(automatic: true); Assert.Equal(calls, reader.HistoryCalls.Count);
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = model.LoadHistoryAsync(); Assert.Equal(1, reader.HistoryCalls[^1].Skip);
        reader.History.SetResult(new([new("second", "second", "author", DateTimeOffset.Now)], false)); await retry;
        Assert.False(model.HistoryLoadFailed); Assert.Equal(2, model.Commits.Count); Assert.Empty(model.Error);
        // A broken reader must not cause an empty-page automatic request loop.
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var empty = model.LoadHistoryAsync(); reader.History.SetResult(new([], true)); await empty;
        Assert.True(model.HistoryLoadFailed); Assert.False(model.CanAutoLoadHistory); Assert.Equal(2, model.Commits.Count);
    }

    [Fact]
    public async Task Stale_page_failure_cannot_pause_a_new_query()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo")); await opening;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = model.LoadHistoryAsync(); var oldSource = reader.History;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.HistoryText = "current"; var current = model.ApplyHistoryQueryAsync();
        reader.History.SetResult(new([new("current", "current", "author", DateTimeOffset.Now)], true)); await current;
        oldSource.SetException(new IOException("stale failed")); await old;
        Assert.False(model.HistoryLoadFailed); Assert.True(model.CanAutoLoadHistory); Assert.Equal("current", Assert.Single(model.Commits).Sha);
    }

    [Fact]
    public async Task Ref_refresh_failure_retries_the_first_page_without_appending_to_old_rows()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo") with { HistoryToken = "old" }); await opening;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = model.LoadHistoryAsync(); reader.History.SetResult(new([new("old", "old", "author", DateTimeOffset.Now)], true)); await first;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = model.RefreshAsync(); reader.Opens["repo"].SetResult(Snapshot("repo") with { HistoryToken = "new" });
        await UntilAsync(() => model.HistoryLoading);
        var calls = reader.HistoryCalls.Count; await model.LoadHistoryAsync(automatic: true); Assert.Equal(calls, reader.HistoryCalls.Count);
        reader.History.SetException(new IOException("refresh failed")); await refresh;
        Assert.True(model.HistoryLoadFailed); Assert.Equal("old", Assert.Single(model.Commits).Sha);
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = model.LoadHistoryAsync(); Assert.Equal(0, reader.HistoryCalls[^1].Skip);
        reader.History.SetResult(new([new("new", "new", "author", DateTimeOffset.Now)], false)); await retry;
        Assert.Equal("new", Assert.Single(model.Commits).Sha); Assert.False(model.HistoryLoadFailed);
    }

    [Fact]
    public async Task Branch_scope_rejects_old_results_and_paginates_the_selected_reference()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var snapshot = Snapshot("repo") with { Head = "head", References = [new("one", "refs/heads/one", "one", "local"), new("two", "refs/heads/two", "two", "local")] };
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(snapshot); await opening;
        model.SwitchView(2);
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = model.SelectHistoryReferenceAsync("refs/heads/one"); var oldSource = reader.History;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = model.SelectHistoryReferenceAsync("refs/heads/two");
        reader.History.SetResult(new([new("two-sha", "current", "author", DateTimeOffset.UtcNow)], true)); await second;
        oldSource.SetResult(new([new("one-sha", "stale", "author", DateTimeOffset.UtcNow)], true)); await first;
        Assert.Equal("two-sha", Assert.Single(model.Commits).Sha); Assert.Equal("two", model.HistoryScope);
        reader.History = null; await model.LoadHistoryAsync();
        Assert.Equal(("two", 1), reader.HistoryCalls[^1]);
        var next = model.OpenAsync("next"); reader.Opens["next"].SetResult(Snapshot("next")); await next;
        Assert.Null(model.HistoryReferenceKey); Assert.Equal("全部分支", model.HistoryScope);
    }

    [Fact]
    public async Task New_history_query_rejects_old_results_and_keeps_filter_on_next_page()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo")); await opening;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.HistoryText = "old"; var old = model.ApplyHistoryQueryAsync(); var oldSource = reader.History;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.HistoryText = "new"; model.HistoryAuthor = "author"; var current = model.ApplyHistoryQueryAsync();
        reader.History.SetResult(new([new("123456789abcdef", "new", "author", DateTimeOffset.Now)], true)); await current;
        oldSource.SetResult(new([new("oldoldoldoldold", "old", "author", DateTimeOffset.Now)], false)); await old;
        Assert.Equal("new", Assert.Single(model.Commits).Subject);
        reader.History = null; await model.LoadHistoryAsync();
        Assert.Equal(new HistoryQuery("new", "author"), reader.Queries[^1]);
        Assert.Equal(1, reader.HistoryCalls[^1].Skip);
    }

    [Fact]
    public async Task Selecting_another_commit_rejects_pending_version_comparison()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo")); await opening;
        model.SwitchView(2);
        var comparison = model.CompareHistoryAsync(new("oldoldoldold", "old", "author", DateTimeOffset.Now), null);
        model.SelectedCommit = new("currentcurrent", "current", "author", DateTimeOffset.Now);
        reader.CommitChanges.SetResult([new("current.txt", ComparisonKind.Commit, "Modified")]);
        await UntilAsync(() => model.Entries.Any(e => e.Path == "current.txt"));
        reader.Comparison.SetResult([new("stale.txt", ComparisonKind.CommitWorktree, "Modified")]); await comparison;
        Assert.Single(model.Entries); Assert.Equal("current.txt", model.Entries[0].Path);
        Assert.Equal("提交中的文件", model.HistoryFilesTitle);
    }
    [Fact]
    public async Task Notices_are_distinct_from_real_file_text_including_loading_and_errors()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var published = new List<RepositoryDocument>(); model.DocumentChanged += (_, doc) => published.Add(doc);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo")); await opening;
        model.SelectedEntry = new("notice.txt", "", ComparisonKind.WorkingFile);
        Assert.True(published[^1].IsNotice);
        // A real file with the same text must still be a code document.
        reader.Documents["notice.txt"].SetResult(Document("notice.txt", "正在读取…"));
        await UntilAsync(() => published.Any(d => d.Path == "notice.txt" && !d.IsNotice));
        model.SelectedEntry = new("missing.txt", "", ComparisonKind.WorkingFile);
        reader.Documents["missing.txt"].SetException(new IOException("文件不可读"));
        await UntilAsync(() => published.Any(d => d.Text == "文件不可读"));
        Assert.True(published[^1].IsNotice);
        model.SelectedEntry = null;
        Assert.True(published[^1].IsNotice);
    }

    [Fact]
    public async Task Slow_old_repository_cannot_replace_the_new_repository()
    {
        var reader = new DelayedReader();
        using var model = new MainViewModel(reader);
        var old = model.OpenAsync("old");
        model.Filter = "old filter";
        var current = model.OpenAsync("new");
        reader.Opens["new"].SetResult(Snapshot("new"));
        await current;
        reader.Opens["old"].SetResult(Snapshot("old"));
        await old;
        Assert.Equal("new", model.RepositoryPath);
        Assert.Equal("new", model.Branch);
        Assert.Equal("", model.Filter);
        Assert.Contains(model.Entries, e => e.Path == "new.txt");
        Assert.DoesNotContain(model.Entries, e => e.Path == "old.txt");
        Assert.False(model.Busy);
    }

    [Fact]
    public async Task Slow_old_file_and_close_cannot_publish_stale_content()
    {
        var reader = new DelayedReader();
        using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo");
        reader.Opens["repo"].SetResult(Snapshot("repo"));
        await opening;
        var published = new List<string>();
        model.DocumentChanged += (_, doc) => published.Add(doc.Text);
        model.SelectedEntry = new("slow.txt", "", ComparisonKind.WorkingFile);
        model.SelectedEntry = new("fast.txt", "", ComparisonKind.WorkingFile);
        reader.Documents["fast.txt"].SetResult(Document("fast.txt", "current"));
        await UntilAsync(() => published.Contains("current"));
        reader.Documents["slow.txt"].SetResult(Document("slow.txt", "stale"));
        await Task.Delay(50);
        Assert.Equal("fast.txt", model.FileTitle);
        Assert.DoesNotContain("stale", published);
        model.SelectedEntry = new("closing.txt", "", ComparisonKind.WorkingFile);
        model.Dispose();
        reader.Documents["closing.txt"].SetResult(Document("closing.txt", "after close"));
        await Task.Delay(50);
        Assert.DoesNotContain("after close", published);
    }

    [Fact]
    public async Task Selecting_directory_clears_document_and_rejects_pending_file_result()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo")); await opening;
        var published = new List<string>(); model.DocumentChanged += (_, doc) => published.Add(doc.Text);
        model.SelectedEntry = new("slow.txt", "", ComparisonKind.WorkingFile);
        model.SelectedEntry = null;
        Assert.Equal("选择文件", model.FileTitle); Assert.False(model.CanBlame); Assert.Null(model.CurrentWrite());
        Assert.Equal("选择文件查看内容。", published[^1]);
        reader.Documents["slow.txt"].SetResult(Document("slow.txt", "stale")); await Task.Delay(50);
        Assert.DoesNotContain("stale", published);
    }

    [Fact]
    public async Task Switching_views_discards_pending_commit_and_file_results()
    {
        var reader = new DelayedReader();
        using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo");
        reader.Opens["repo"].SetResult(Snapshot("repo")); await opening;
        model.SwitchView(2);
        model.SelectedCommit = new("123456789abcdef", "subject", "author", DateTimeOffset.UtcNow);
        model.SwitchView(0);
        reader.CommitChanges.SetResult([new("historical.txt", ComparisonKind.Commit, "Modified")]);
        await Task.Delay(50);
        Assert.Contains(model.Entries, e => e.Path == "repo.txt");
        Assert.DoesNotContain(model.Entries, e => e.Path == "historical.txt");
    }

    [Fact]
    public async Task Late_history_page_cannot_append_to_refreshed_graph()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo") with { HistoryToken = "old" }); await opening;
        reader.History = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPage = model.LoadHistoryAsync(); var oldSource = reader.History;
        reader.History = null;
        var refresh = model.RefreshAsync(); reader.Opens["repo"].SetResult(Snapshot("repo") with { HistoryToken = "new" }); await refresh;
        oldSource.SetResult(new([new("123456789abcdef", "stale page", "author", DateTimeOffset.UtcNow)], true)); await oldPage;
        Assert.Empty(model.Commits); Assert.False(model.HasMoreHistory);
    }

    [Fact]
    public async Task Removed_commit_cannot_publish_late_files_after_ref_refresh()
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader);
        var opening = model.OpenAsync("repo"); reader.Opens["repo"].SetResult(Snapshot("repo") with { HistoryToken = "old" }); await opening;
        model.SwitchView(2); model.SelectedCommit = new("123456789abcdef", "removed", "author", DateTimeOffset.UtcNow);
        var refresh = model.RefreshAsync(); reader.Opens["repo"].SetResult(Snapshot("repo") with { HistoryToken = "new" }); await refresh;
        reader.CommitChanges.SetResult([new("stale.txt", ComparisonKind.Commit, "Modified")]); await Task.Delay(50);
        Assert.Null(model.SelectedCommit); Assert.Empty(model.Entries);
    }

    private static RepositorySnapshot Snapshot(string root) => new(root, root, null, [new(root + ".txt")], [], false);
    private static RepositoryDocument Document(string path, string text) => new(path, text, "plaintext", "working", "");
    private static async Task UntilAsync(Func<bool> completed)
    {
        for (var i = 0; i < 100 && !completed(); i++) await Task.Delay(10);
        Assert.True(completed());
    }

    private sealed class DelayedReader : IRepositoryReader
    {
        public Dictionary<string, TaskCompletionSource<RepositorySnapshot>> Opens { get; } = [];
        public Dictionary<string, TaskCompletionSource<RepositoryDocument>> Documents { get; } = [];
        public TaskCompletionSource<IReadOnlyList<ChangeEntry>> CommitChanges { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HistoryPage>? History { get; set; }
        public List<(string? Head, int Skip)> HistoryCalls { get; } = [];
        public List<HistoryQuery> Queries { get; } = [];
        public TaskCompletionSource<IReadOnlyList<ChangeEntry>> Comparison { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default)
        {
            var source = new TaskCompletionSource<RepositorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Opens[path] = source; return source.Task;
        }
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default)
        {
            var source = new TaskCompletionSource<RepositoryDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            Documents.Add(request.Path, source); return source.Task;
        }
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default)
        { HistoryCalls.Add((head, skip)); return History?.Task ?? Task.FromResult(new HistoryPage([], false)); }
        public Task<HistoryPage> QueryHistoryAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
        { Queries.Add(query); return HistoryAsync(root, head, skip, token); }
        public Task<IReadOnlyList<ChangeEntry>> CompareChangesAsync(string root, string oldCommit, string? newCommit, CancellationToken token = default) => Comparison.Task;
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => CommitChanges.Task;
    }
}
