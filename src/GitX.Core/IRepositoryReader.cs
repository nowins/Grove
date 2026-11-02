namespace GitX.Core;

public interface IRepositoryReader
{
    Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default);
    Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default);
    Task<HistoryPage> QueryHistoryAsync(string root, string? head, HistoryQuery query, int skip = 0, CancellationToken token = default)
        => query.IsEmpty ? HistoryAsync(root, head, skip, token) : throw new NotSupportedException("此读取器不支持历史筛选。");
    Task<IReadOnlyList<ChangeEntry>> CompareChangesAsync(string root, string oldCommit, string? newCommit, CancellationToken token = default)
        => throw new NotSupportedException("此读取器不支持版本比较。");
    Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default);
    Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default);
}
