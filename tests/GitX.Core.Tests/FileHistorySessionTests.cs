using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class FileHistorySessionTests
{
    [Fact]
    public async Task File_history_selects_inline_diffs_without_changing_main_source_or_git_state()
    {
        using var repo = new TestRepository();
        repo.Write("src/a.cs", "base\n"); repo.Write("b.cs", "other\n"); repo.Commit("base");
        repo.Write("b.cs", "changed\n"); repo.Commit("unrelated"); repo.Write("src/a.cs", "next\n"); repo.Commit("change a");
        var head = repo.Git("rev-parse", "HEAD"); var index = repo.Git("write-tree");
        using var main = new MainViewModel(); await main.OpenAsync(repo.Root);
        main.Filter = "src/"; main.SelectedEntry = Assert.Single(main.Entries);
        await UntilAsync(() => main.CurrentDocument?.Text == "next\n");
        var source = main.CurrentDocument; var tab = main.ActiveSourceTabId; var version = main.DocumentVersion;
        using (var history = new FileHistoryViewModel(new(repo.Root, "src/a.cs")))
        {
            await history.RefreshAsync();
            Assert.Equal(new[] { "change a", "base" }, history.Commits.Select(c => c.Subject));
            Assert.Equal("next\n", history.Document!.Diff!.New.Text);
            Assert.Equal("base\n", history.Document.Diff.Old.Text);
            Assert.Null(history.Document.CommentContext); Assert.False(history.Document.CanStage);
            await history.SelectAsync(history.Commits[1]);
            Assert.Equal("base\n", history.Document.Diff!.New.Text); Assert.False(history.Document.Diff.Old.Exists);
        }
        Assert.Equal(0, main.Mode); Assert.Same(source, main.CurrentDocument);
        Assert.Equal(version, main.DocumentVersion); Assert.Equal(tab, main.ActiveSourceTabId); Assert.Equal("src/", main.Filter);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("write-tree"));
    }

    [Fact]
    public async Task Deleted_file_has_a_deletion_diff_and_an_untracked_file_has_an_empty_history()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "base\n"); repo.Commit("base");
        repo.Git("rm", "a.cs"); repo.Commit("delete"); repo.Write("new.cs", "untracked\n");
        using var deleted = new FileHistoryViewModel(new(repo.Root, "a.cs")); await deleted.RefreshAsync();
        Assert.Equal("delete", deleted.SelectedCommit!.Subject);
        Assert.True(deleted.Document!.Diff!.Old.Exists); Assert.False(deleted.Document.Diff.New.Exists);
        using var untracked = new FileHistoryViewModel(new(repo.Root, "new.cs")); await untracked.RefreshAsync();
        Assert.Empty(untracked.Commits); Assert.True(untracked.Document!.IsNotice); Assert.Contains("暂无", untracked.Status);
    }

    [Fact]
    public async Task A_late_old_revision_cannot_replace_the_new_selection_even_if_reader_ignores_cancellation()
    {
        var reader = new PendingReader(); using var model = new FileHistoryViewModel(new("repo", "a.txt"), reader);
        var first = model.RefreshAsync(); await reader.FirstStarted.Task;
        var second = model.SelectAsync(model.Commits[1]);
        reader.Second.SetResult(new("a.txt", "second", "plaintext", "", "", CanStage: true)); await second;
        var version = model.DocumentVersion;
        reader.First.SetResult(new("a.txt", "late first", "plaintext", "", "")); await first;
        Assert.Equal("second", model.Document!.Text); Assert.Equal(version, model.DocumentVersion); Assert.False(model.Document.CanStage);
        Assert.True(reader.FirstToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Closing_a_session_cancels_reads_and_suppresses_late_publication()
    {
        var reader = new PendingReader(); var model = new FileHistoryViewModel(new("repo", "a.txt"), reader);
        var loading = model.RefreshAsync(); await reader.FirstStarted.Task;
        var publications = 0; model.DocumentChanged += (_, _) => publications++;
        model.Dispose(); reader.First.SetResult(new("a.txt", "late", "plaintext", "", "")); await loading;
        Assert.True(reader.FirstToken.IsCancellationRequested); Assert.Equal(0, publications);
    }

    [Fact]
    public async Task Paging_keeps_the_selection_and_refresh_replaces_the_snapshot()
    {
        var reader = new PagedReader(); using var model = new FileHistoryViewModel(new("repo", "a.txt"), reader);
        await model.RefreshAsync(); Assert.Single(model.Commits); Assert.True(model.HasMore);
        var selected = model.SelectedCommit; var version = model.DocumentVersion;
        await model.LoadMoreAsync(automatic: true); Assert.Equal(2, model.Commits.Count); Assert.Same(selected, model.SelectedCommit);
        Assert.Equal(version, model.DocumentVersion); Assert.False(model.HasMore);
        var calls = reader.Calls.Count; await model.LoadMoreAsync(automatic: true); Assert.Equal(calls, reader.Calls.Count);
        reader.Unborn = true; await model.RefreshAsync();
        Assert.Empty(model.Commits); Assert.Null(model.SelectedCommit); Assert.True(model.Document!.IsNotice);
        Assert.All(reader.Queries, q => Assert.Equal("a.txt", q.Path));
    }

    [Fact]
    public async Task Automatic_paging_is_single_flight_and_failure_pauses_until_retry_with_the_same_scope()
    {
        var reader = new GatedPageReader(); var until = new string('f', 40);
        using var model = new FileHistoryViewModel(new("repo", "a.txt", until), reader);
        await model.RefreshAsync(); var selected = model.SelectedCommit; var document = model.Document; var version = model.DocumentVersion;
        reader.Page = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = model.LoadMoreAsync(automatic: true); var calls = reader.Calls.Count;
        Assert.True(model.Loading); Assert.False(model.CanAutoLoad);
        await model.LoadMoreAsync(automatic: true); Assert.Equal(calls, reader.Calls.Count);
        reader.Page.SetException(new IOException("page failed")); await page;
        Assert.True(model.LoadFailed); Assert.Equal("page failed", model.LoadError); Assert.False(model.CanAutoLoad);
        await model.LoadMoreAsync(automatic: true); Assert.Equal(calls, reader.Calls.Count);
        Assert.Same(document, model.Document); Assert.Equal(version, model.DocumentVersion);
        reader.Page = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = model.RetryAsync(); Assert.Equal((until, 1), reader.Calls[^1]);
        reader.Page.SetResult(new([Commit('b')], false)); await retry;
        Assert.Equal(2, model.Commits.Count); Assert.Same(selected, model.SelectedCommit); Assert.Same(document, model.Document);
        Assert.False(model.LoadFailed); Assert.Null(model.LoadError); Assert.Equal(version, model.DocumentVersion);
        Assert.All(reader.Queries, q => Assert.Equal("a.txt", q.Path));
    }

    [Fact]
    public async Task Failed_first_page_can_retry_even_without_a_next_page()
    {
        var reader = new GatedPageReader { GateFirst = true, Page = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var model = new FileHistoryViewModel(new("repo", "a.txt"), reader);
        var first = model.RefreshAsync(); reader.Page.SetException(new IOException("initial failure")); await first;
        Assert.True(model.LoadFailed); Assert.False(model.HasMore); Assert.True(model.Document!.IsNotice);
        reader.GateFirst = false; await model.RetryAsync();
        Assert.Equal(0, reader.Calls[^1].Skip); Assert.False(model.LoadFailed); Assert.True(model.CanAutoLoad);
        Assert.Single(model.Commits); Assert.False(model.Document!.IsNotice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_and_close_reject_late_page_results_and_errors(bool failure)
    {
        var reader = new GatedPageReader(); var model = new FileHistoryViewModel(new("repo", "a.txt"), reader);
        await model.RefreshAsync(); reader.Page = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = model.LoadMoreAsync(automatic: true); var oldPage = reader.Page; var oldToken = reader.LastToken;
        await model.RefreshAsync(); var version = model.DocumentVersion;
        if (failure) oldPage.SetException(new IOException("stale failure")); else oldPage.SetResult(new([Commit('z')], false));
        await old;
        Assert.True(oldToken.IsCancellationRequested); Assert.False(model.LoadFailed); Assert.True(model.CanAutoLoad);
        Assert.Equal('a', Assert.Single(model.Commits).Sha[0]); Assert.Equal(version, model.DocumentVersion);
        reader.Page = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = model.LoadMoreAsync(automatic: true); model.Dispose();
        reader.Page.SetResult(new([Commit('z')], false)); await closing;
        Assert.True(reader.LastToken.IsCancellationRequested); Assert.Single(model.Commits); Assert.Equal(version, model.DocumentVersion);
    }

    [Fact]
    public async Task Empty_page_with_more_flag_stops_automatic_request_loops()
    {
        var reader = new GatedPageReader(); using var model = new FileHistoryViewModel(new("repo", "a.txt"), reader);
        await model.RefreshAsync(); reader.Page = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = model.LoadMoreAsync(automatic: true); reader.Page.SetResult(new([], true)); await page;
        Assert.True(model.LoadFailed); Assert.False(model.CanAutoLoad); Assert.Single(model.Commits);
        var calls = reader.Calls.Count; await model.LoadMoreAsync(automatic: true); Assert.Equal(calls, reader.Calls.Count);
    }
    private static async Task UntilAsync(Func<bool> predicate)
    { for (var i = 0; i < 300 && !predicate(); i++) await Task.Delay(10); Assert.True(predicate()); }
    private static CommitEntry Commit(char c) => new(new string(c, 40), c.ToString(), "author", DateTimeOffset.UtcNow);
    private class PagedReader : IRepositoryReader
    {
        public bool Unborn;
        public List<HistoryQuery> Queries { get; } = [];
        public List<(string? Head, int Skip)> Calls { get; } = [];
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", Unborn ? null : new string('a', 40), [], [], false));
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage([], false));
        public virtual Task<HistoryPage> QueryHistoryAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
        { Queries.Add(query); Calls.Add((head, skip)); return Task.FromResult(head is null ? new HistoryPage([], false) : new HistoryPage([Commit(skip == 0 ? 'a' : 'b')], skip == 0)); }
        public virtual Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default) => Task.FromResult(new RepositoryDocument(request.Path, request.Commit!, "plaintext", "", ""));
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class GatedPageReader : PagedReader
    {
        public TaskCompletionSource<HistoryPage>? Page;
        public bool GateFirst;
        public CancellationToken LastToken;
        public override Task<HistoryPage> QueryHistoryAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
        {
            if (Page is null || skip == 0 && !GateFirst) return base.QueryHistoryAsync(root, head, query, skip, token);
            Queries.Add(query); Calls.Add((head, skip)); LastToken = token; return Page.Task;
        }
    }
    private sealed class PendingReader : PagedReader
    {
        public TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RepositoryDocument> First = new(TaskCreationOptions.RunContinuationsAsynchronously), Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstToken;
        public override Task<HistoryPage> QueryHistoryAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
            => Task.FromResult(new HistoryPage([Commit('a'), Commit('b')], false));
        public override Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default)
        { if (request.Commit![0] == 'b') return Second.Task; FirstToken = token; FirstStarted.SetResult(); return First.Task; }
    }
}
