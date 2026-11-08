using System.Text.Json;
using GitX.Core;
using GitX.Desktop.ViewModels;
using LibGit2Sharp;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryDetailsTests
{
    [Fact]
    public async Task Summaries_preserve_paging_graph_body_search_and_legacy_full_messages()
    {
        using var fixture = new TestRepository(); fixture.Write("file.txt", "base"); fixture.Commit("base\n\nbody-only needle");
        using (var repo = new Repository(fixture.Root))
        {
            var author = new Signature("test", "test@example.invalid", DateTimeOffset.UtcNow);
            for (var i = 0; i < 101; i++) repo.Commit($"subject {i}\n\nlong body {i}", author, author, new CommitOptions { AllowEmptyCommit = true });
        }
        using var reader = new RepositoryReader();
        var snapshot = await reader.OpenAsync(fixture.Root);
        var summaries = await reader.QueryHistorySummariesAsync(fixture.Root, snapshot.HistoryHeads, new());
        var tail = await reader.QueryHistorySummariesAsync(fixture.Root, snapshot.HistoryHeads, new(), 100);
        Assert.Equal(100, summaries.Commits.Count); Assert.True(summaries.HasMore);
        Assert.Equal(2, tail.Commits.Count); Assert.False(tail.HasMore);
        Assert.All(summaries.Commits.Concat(tail.Commits), c => { Assert.False(c.HasFullMessage); Assert.Empty(c.Message); });
        var full = await reader.HistoryAsync(fixture.Root, snapshot.HistoryHeads);
        Assert.Equal(JsonSerializer.Serialize(full.Commits.Select(c => c with { Message = "", HasFullMessage = false })), JsonSerializer.Serialize(summaries.Commits));
        Assert.All(full.Commits, c => Assert.True(c.HasFullMessage));
        foreach (var query in new[] { new HistoryQuery(Text: "body-only needle"), new HistoryQuery(Text: "body-only.*needle", UseRegex: true) })
        {
            var match = Assert.Single((await reader.QueryHistorySummariesAsync(fixture.Root, snapshot.HistoryHeads, query)).Commits);
            Assert.False(match.HasFullMessage);
            Assert.Equal("base\n\nbody-only needle\n", await reader.CommitMessageAsync(fixture.Root, match.Sha));
            Assert.Empty(match.Message); // Reading a detail never enriches all retained list rows.
        }
    }

    [Fact]
    public async Task Cache_resolves_refs_shallow_parents_and_rejects_cancelled_or_missing_reads()
    {
        using var fixture = new TestRepository(); fixture.Write("a.txt", "first"); fixture.Commit("first\n\nbody one");
        var first = fixture.Git("rev-parse", "HEAD").Trim();
        using var reader = new RepositoryReader();
        var initial = await reader.CommitChangesAsync(fixture.Root, "HEAD");
        Assert.Same(initial, await reader.CommitChangesAsync(fixture.Root, first[..12]));
        Assert.Contains("body one", await reader.CommitMessageAsync(fixture.Root, "HEAD"));
        fixture.Write("b.txt", "second"); fixture.Commit("second\n\nbody two");
        var second = fixture.Git("rev-parse", "HEAD").Trim();
        Assert.Equal("b.txt", Assert.Single(await reader.CommitChangesAsync(fixture.Root, "HEAD")).Path);
        Assert.Contains("body two", await reader.CommitMessageAsync(fixture.Root, "HEAD"));
        var normal = await reader.CommitChangesAsync(fixture.Root, second);
        File.WriteAllText(Path.Combine(fixture.Root, ".git", "shallow"), second + "\n");
        Assert.Equal(2, (await reader.CommitChangesAsync(fixture.Root, second)).Count);
        File.Delete(Path.Combine(fixture.Root, ".git", "shallow"));
        Assert.Same(normal, await reader.CommitChangesAsync(fixture.Root, second));
        await Assert.ThrowsAsync<IOException>(() => reader.CommitChangesAsync(fixture.Root, "later"));
        fixture.Git("branch", "later", first);
        Assert.Same(initial, await reader.CommitChangesAsync(fixture.Root, "later"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.CommitChangesAsync(fixture.Root, first, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.CommitMessageAsync(fixture.Root, first, cancelled.Token));
        reader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.CommitChangesAsync(fixture.Root, first));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.CommitMessageAsync(fixture.Root, first));
    }

    [Fact]
    public async Task Cached_tree_results_are_readonly_kind_and_worktree_isolated_and_match_git()
    {
        using var fixture = new TestRepository(); fixture.Write("中文 file.txt", "first"); fixture.Write("mode.txt", "mode"); fixture.Commit("first");
        var first = fixture.Git("rev-parse", "HEAD").Trim();
        fixture.Git("mv", "中文 file.txt", "renamed.txt"); fixture.Git("update-index", "--chmod=+x", "mode.txt");
        fixture.Git("commit", "-m", "rename and mode");
        var second = fixture.Git("rev-parse", "HEAD").Trim();
        using var reader = new RepositoryReader();
        var entries = await reader.CommitChangesAsync(fixture.Root, second);
        Assert.Equal(new[] { "mode.txt", "renamed.txt", "中文 file.txt" }, entries.Select(c => c.Path));
        Assert.All(entries, c => { Assert.Equal(c.Path, c.OldPath); Assert.Same(c.Path, c.OldPath); });
        Assert.Throws<NotSupportedException>(() => ((IList<ChangeEntry>)entries)[0] = new("corrupt", ComparisonKind.Commit, "Added"));
        var range = await reader.CompareChangesAsync(fixture.Root, first, second);
        Assert.Same(range, await reader.CompareChangesAsync(fixture.Root, first, second));
        Assert.All(range, c => Assert.Equal(ComparisonKind.RevisionRange, c.Kind));
        Assert.Equal(entries.Select(c => c with { Kind = ComparisonKind.RevisionRange }), range);
        fixture.Git("config", "core.ignorecase", "true"); fixture.Git("config", "core.filemode", "false");
        using var fresh = new RepositoryReader();
        Assert.Equal(entries, await fresh.CommitChangesAsync(fixture.Root, second));
        var worktree = Path.Combine(fixture.Container, "linked"); fixture.Git("worktree", "add", "--detach", worktree, second);
        var linked = await reader.CommitChangesAsync(worktree, second);
        Assert.Equal(entries, linked); Assert.NotSame(entries, linked);
        Assert.Empty(await reader.CompareChangesAsync(fixture.Root, second, null));
        fixture.Write("new.txt", "untracked");
        Assert.Equal("new.txt", Assert.Single(await reader.CompareChangesAsync(fixture.Root, second, null)).Path);
        Assert.Same(entries, await reader.CommitChangesAsync(fixture.Root, second));
        Assert.Equal(second, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(second, fixture.Git("-C", worktree, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Combined_details_resolve_real_messages_and_preserve_commit_range_and_live_worktree_semantics()
    {
        using var fixture = new TestRepository(); fixture.Write("a.txt", "one"); fixture.Commit("one\n\nfirst body");
        var first = fixture.Git("rev-parse", "HEAD").Trim();
        fixture.Write("a.txt", "two"); fixture.Commit("two\n\nsecond body");
        var second = fixture.Git("rev-parse", "HEAD").Trim();
        using var reader = new RepositoryReader();
        var old = new CommitEntry(first, "one", "author", DateTimeOffset.UtcNow, Message: "stale display data");
        var next = old with { Sha = second, Subject = "two", Message = "", HasFullMessage = false };
        var details = await reader.CommitDetailsAsync(fixture.Root, next);
        Assert.Equal(await reader.CommitMessageAsync(fixture.Root, second), details.Message);
        Assert.Same(await reader.CommitChangesAsync(fixture.Root, second), details.Changes);
        var range = await reader.CompareDetailsAsync(fixture.Root, old, next);
        Assert.Contains("first body", range.OldMessage); Assert.Contains("second body", range.NewMessage);
        Assert.Same(await reader.CompareChangesAsync(fixture.Root, first, second), range.Changes);
        var working = await reader.CompareDetailsAsync(fixture.Root, next, null);
        Assert.Null(working.NewMessage); Assert.Empty(working.Changes);
        fixture.Write("a.txt", "three");
        working = await reader.CompareDetailsAsync(fixture.Root, next, null);
        Assert.Equal("a.txt", Assert.Single(working.Changes).Path); Assert.Equal(details.Message, working.OldMessage);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.CommitDetailsAsync(fixture.Root, next, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.CompareDetailsAsync(fixture.Root, old, next, cancelled.Token));
        Assert.Equal(second, fixture.Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public void Cache_has_byte_and_entry_limits_promotes_hits_and_cannot_retain_after_disposal()
    {
        using var cache = new HistoryReadCache<string, object>(2, 10);
        var a = new object(); var b = new object(); var c = new object();
        cache.Put("a", a, 4); cache.Put("b", b, 4);
        Assert.True(cache.TryGet("a", out var hit)); Assert.Same(a, hit);
        cache.Put("c", c, 4); Assert.False(cache.TryGet("b", out _));
        cache.Put("huge", new(), 11); Assert.True(cache.TryGet("a", out _)); Assert.True(cache.TryGet("c", out _));
        cache.Put("c", c, 8); Assert.False(cache.TryGet("a", out _));
        cache.Put("tiny1", new(), 0); cache.Put("tiny2", new(), 0); Assert.False(cache.TryGet("c", out _));
        cache.Dispose(); cache.Put("late", new(), 1); Assert.False(cache.TryGet("late", out _)); Assert.False(cache.TryGet("tiny1", out _));
    }

    [Fact]
    public async Task Main_history_loads_only_selected_messages_and_drops_stale_results()
    {
        var reader = new DetailReader(); using var model = new MainViewModel(reader);
        await model.OpenAsync("first"); model.SwitchView(2);
        Assert.All(model.Commits, c => Assert.False(c.HasFullMessage)); Assert.Empty(reader.Messages);
        model.SelectedCommit = model.Commits[0];
        var old = reader.Messages[("first", "111111111111")];
        Assert.Contains("正在读取", model.HistoryCommitDetail);
        model.SelectedCommit = model.Commits[1];
        reader.Messages[("first", "222222222222")].SetResult("two\n\ncurrent detail");
        await UntilAsync(() => model.HistoryCommitDetail.Contains("current detail"));
        old.SetResult("one\n\nstale detail"); await Task.Delay(30);
        Assert.DoesNotContain("stale detail", model.HistoryCommitDetail);
        Assert.All(model.Commits, c => Assert.Empty(c.Message));
        model.SelectedCommit = model.Commits[0];
        var late = reader.Messages[("first", "111111111111")];
        await model.OpenAsync("second"); late.SetResult("one\n\nold repository"); await Task.Delay(30);
        Assert.DoesNotContain("old repository", model.HistoryCommitDetail); Assert.Empty(model.Entries);
    }

    [Fact]
    public async Task Failed_detail_can_be_retried_by_full_refresh_without_reselecting()
    {
        var reader = new DetailReader(); using var model = new MainViewModel(reader); await model.OpenAsync("first"); model.SwitchView(2);
        model.SelectedCommit = model.Commits[0];
        var failed = reader.Messages[("first", "111111111111")]; failed.SetException(new IOException("temporary read failure"));
        await UntilAsync(() => model.HistoryCommitDetail.Contains("读取失败"));
        Assert.Contains("temporary read failure", model.Error);
        var refresh = model.RefreshAsync();
        await UntilAsync(() => !ReferenceEquals(failed, reader.Messages[("first", "111111111111")]));
        reader.Messages[("first", "111111111111")].SetResult("one\n\nrecovered body"); await refresh;
        Assert.Contains("recovered body", model.HistoryCommitDetail); Assert.Empty(model.Error);
    }

    [Fact]
    public async Task Comparison_details_wait_for_both_messages_and_cannot_replace_new_selection()
    {
        var reader = new DetailReader(); using var model = new MainViewModel(reader); await model.OpenAsync("first"); model.SwitchView(2);
        var old = model.Commits[0]; var next = model.Commits[1];
        var comparing = model.CompareHistoryAsync(old, next);
        var oldMessage = reader.Messages[("first", old.Sha)]; var nextMessage = reader.Messages[("first", next.Sha)];
        oldMessage.SetResult("one\n\nold body"); nextMessage.SetResult("two\n\nnew body"); await comparing;
        Assert.Contains("old body", model.HistoryCommitDetail); Assert.Contains("new body", model.HistoryCommitDetail);
        comparing = model.CompareHistoryAsync(old, next);
        oldMessage = reader.Messages[("first", old.Sha)]; nextMessage = reader.Messages[("first", next.Sha)];
        model.SelectedCommit = next; reader.Messages[("first", next.Sha)].SetResult("two\n\nselected body");
        await UntilAsync(() => model.HistoryCommitDetail.Contains("selected body"));
        oldMessage.SetResult("stale old"); nextMessage.SetResult("stale next"); await comparing;
        Assert.DoesNotContain("stale", model.HistoryCommitDetail); Assert.Contains("selected body", model.HistoryCommitDetail);
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        for (var i = 0; i < 100 && !ready(); i++) await Task.Delay(10);
        Assert.True(ready());
    }

    private sealed class DetailReader : IRepositoryReader
    {
        private readonly CommitEntry[] _commits = [new("111111111111", "one", "a", DateTimeOffset.UtcNow) { HasFullMessage = false }, new("222222222222", "two", "a", DateTimeOffset.UtcNow) { HasFullMessage = false }];
        public Dictionary<(string Root, string Sha), TaskCompletionSource<string>> Messages { get; } = [];
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", _commits[0].Sha, [], [], false));
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => throw new InvalidOperationException("Main history must request summaries.");
        public Task<HistoryPage> QueryHistorySummariesAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage(_commits, false));
        public Task<string> CommitMessageAsync(string root, string sha, CancellationToken token = default)
        { var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); Messages[(root, sha)] = source; return source.Task; }
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([new(sha + ".txt", ComparisonKind.Commit, "Modified")]);
        public Task<IReadOnlyList<ChangeEntry>> CompareChangesAsync(string root, string oldCommit, string? newCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default) => throw new NotSupportedException();
    }
}
