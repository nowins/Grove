using GitX.Core;
using GitX.Desktop.ViewModels;
using LibGit2Sharp;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryCommitNavigationTests
{
    [Fact]
    public async Task Unloaded_target_is_selected_with_correct_graph_and_can_keep_paging()
    {
        using var fixture = new TestRepository(); fixture.Write("a", "base"); fixture.Commit("base");
        string target;
        using (var repo = new Repository(fixture.Root))
        {
            var author = new Signature("test", "test@example.invalid", DateTimeOffset.UtcNow);
            target = repo.Head.Tip.Sha;
            for (var i = 0; i < 204; i++) repo.Commit($"padding {i}", author, author, new CommitOptions { AllowEmptyCommit = true });
        }
        using var model = new MainViewModel(); await model.OpenAsync(fixture.Root);
        Assert.Equal(100, model.Commits.Count);
        await model.NavigateHistoryCommitAsync(target);
        Assert.Equal(target, model.SelectedCommit?.Sha); Assert.Equal(205, model.Commits.Count); Assert.False(model.HasMoreHistory);
        Assert.Equal(205, model.Commits.Select(c => c.Sha).Distinct().Count()); Assert.All(model.Commits, c => Assert.NotNull(c.Graph));
        Assert.Empty(model.Error);
    }

    [Fact]
    public async Task Hidden_target_clears_filters_only_when_needed_and_does_not_checkout()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base"); var first = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "second"); repo.Commit("second"); var head = repo.Git("rev-parse", "HEAD").Trim();
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.SelectHistoryReferenceAsync("HEAD"); model.HistoryText = "second"; await model.ApplyHistoryQueryAsync();
        await model.NavigateHistoryCommitAsync(head);
        Assert.True(model.HasHistoryFilters); Assert.Equal("HEAD", model.HistoryReferenceKey);
        await model.NavigateHistoryCommitAsync(first);
        Assert.Equal(first, model.SelectedCommit?.Sha); Assert.False(model.HasHistoryFilters); Assert.Null(model.HistoryReferenceKey);
        Assert.Equal("", model.HistoryText); Assert.Contains("已清除历史筛选", model.Status);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD").Trim()); Assert.Equal("second", File.ReadAllText(Path.Combine(repo.Root, "a")));
        Assert.Empty(model.Error);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("filter")]
    [InlineData("repository")]
    public async Task Delayed_jump_cannot_override_new_user_intent(string change)
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo"); model.SwitchView(2);
        model.SelectedCommit = model.Commits[0];
        reader.Page = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new string('c', 40);
        var pending = model.NavigateHistoryCommitAsync(target);
        Assert.False(pending.IsCompleted);
        if (change == "selection") model.SelectedCommit = model.Commits[1];
        if (change == "filter") { reader.Block = false; model.HistoryText = "filter"; await model.ApplyHistoryQueryAsync(); }
        if (change == "repository") { reader.Block = false; await model.OpenAsync("other"); }
        var current = model.SelectedCommit?.Sha;
        reader.Page.SetResult(new([new(target, "target", "a", DateTimeOffset.UtcNow)], false));
        await pending;
        Assert.Equal(current, model.SelectedCommit?.Sha); Assert.DoesNotContain(model.Commits, c => c.Sha == target);
        if (change == "filter") Assert.Equal("filter", model.HistoryText);
        if (change == "repository") Assert.Equal("other", model.Root);
    }

    private sealed class DelayedReader : IRepositoryReader
    {
        private readonly CommitEntry[] _commits = [new(new string('a', 40), "one", "a", DateTimeOffset.UtcNow), new(new string('b', 40), "two", "a", DateTimeOffset.UtcNow)];
        public TaskCompletionSource<HistoryPage>? Page;
        public bool Block = true;
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", _commits[0].Sha, [], [], false));
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage(_commits, false));
        public Task<HistoryPage> QueryHistorySummariesAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
            => Block && Page is not null ? Page.Task : HistoryAsync(root, head, skip, token);
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default) => Task.FromResult(new RepositoryDocument(request.Path, "", "plaintext", "", ""));
    }
}
