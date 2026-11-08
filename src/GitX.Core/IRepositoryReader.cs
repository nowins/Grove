namespace GitX.Core;

public interface IRepositoryReader
{
    Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default);
    async Task<RepositoryRefreshResult> RefreshAsync(RepositorySnapshot previous, RepositoryChangeSet changes, CancellationToken token = default)
        => new(await OpenAsync(previous.Root, token).ConfigureAwait(false), RepositoryChangeSet.Full);
    Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default);
    Task<HistoryPage> QueryHistoryAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
        => query.IsEmpty ? HistoryAsync(root, head, skip, token) : throw new NotSupportedException("此读取器不支持历史筛选。");
    Task<HistoryPage> QueryHistorySummariesAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
        => QueryHistoryAsync(root, head, query, skip, token);
    Task<string> CommitMessageAsync(string root, string sha, CancellationToken token = default)
        => throw new NotSupportedException("此读取器不支持按需读取提交说明。");
    async Task<CommitDetails> CommitDetailsAsync(string root, CommitEntry commit, CancellationToken token = default)
    {
        var changes = CommitChangesAsync(root, commit.Sha, token);
        var message = commit.HasFullMessage ? Task.FromResult(commit.Message) : CommitMessageAsync(root, commit.Sha, token);
        await Task.WhenAll(changes, message).ConfigureAwait(false);
        return new(message.Result, changes.Result);
    }
    async Task<CommitComparisonDetails> CompareDetailsAsync(string root, CommitEntry old, CommitEntry? next, CancellationToken token = default)
    {
        var changes = CompareChangesAsync(root, old.Sha, next?.Sha, token);
        var oldMessage = old.HasFullMessage ? Task.FromResult(old.Message) : CommitMessageAsync(root, old.Sha, token);
        var nextMessage = next is null || next.HasFullMessage ? Task.FromResult(next?.Message ?? "") : CommitMessageAsync(root, next.Sha, token);
        await Task.WhenAll(changes, oldMessage, nextMessage).ConfigureAwait(false);
        return new(oldMessage.Result, next is null ? null : nextMessage.Result, changes.Result);
    }
    Task<IReadOnlyList<ChangeEntry>> CompareChangesAsync(string root, string oldCommit, string? newCommit, CancellationToken token = default)
        => throw new NotSupportedException("此读取器不支持版本比较。");
    Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default);
    Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default);
}
