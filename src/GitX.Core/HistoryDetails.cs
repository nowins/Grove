using LibGit2Sharp;

namespace GitX.Core;

public sealed partial class RepositoryReader
{
    private readonly record struct MessageKey(string Repository, string Commit);
    private readonly record struct ChangesKey(string Repository, string? BeforeTree, string AfterTree, ComparisonKind Kind);
    private readonly HistoryReadCache<MessageKey, string> _commitMessages = new(64, 1024 * 1024);
    private readonly HistoryReadCache<ChangesKey, IReadOnlyList<ChangeEntry>> _commitChanges = new(32, 4 * 1024 * 1024, frequencyAdmission: true);

    public Task<string> CommitMessageAsync(string root, string sha, CancellationToken token = default) => ReadWork.Cpu.Run(() =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var repo = new Repository(root);
        // Resolve every time, including cache hits: HEAD, refs and abbreviations
        // are mutable, and deleted repositories/objects must still be rejected.
        var commit = repo.Lookup<Commit>(sha) ?? throw new IOException("提交已不存在，请刷新仓库。");
        return ReadCommitMessage(repo, commit, token);
    }, token);

    public Task<CommitDetails> CommitDetailsAsync(string root, CommitEntry entry, CancellationToken token = default) => ReadWork.Cpu.Run(() =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var repo = new Repository(root);
        var commit = repo.Lookup<Commit>(entry.Sha) ?? throw new IOException("提交已不存在，请刷新仓库。");
        // One capture for all selected details, instead of reopening the same
        // repository independently for the message and the changed-file list.
        var message = ReadCommitMessage(repo, commit, token);
        var changes = ReadTreeChanges(repo, commit.Parents.FirstOrDefault()?.Tree, commit.Tree, ComparisonKind.Commit, token);
        return new CommitDetails(message, changes);
    }, token);

    public async Task<CommitComparisonDetails> CompareDetailsAsync(string root, CommitEntry old, CommitEntry? next, CancellationToken token = default)
    {
        if (next is not null) return await ReadWork.Cpu.Run(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var repo = new Repository(root);
            var before = repo.Lookup<Commit>(old.Sha) ?? throw new IOException("比较起点已不存在。");
            var after = repo.Lookup<Commit>(next.Sha) ?? throw new IOException("比较终点已不存在。");
            var oldMessage = ReadCommitMessage(repo, before, token);
            var newMessage = ReadCommitMessage(repo, after, token);
            var changes = ReadTreeChanges(repo, before.Tree, after.Tree, ComparisonKind.RevisionRange, token);
            return new CommitComparisonDetails(oldMessage, newMessage, changes);
        }, token).ConfigureAwait(false);

        // The worktree list remains a live Git read; it is never cached with
        // the immutable commit message.
        var files = CompareChangesAsync(root, old.Sha, null, token);
        var message = CommitMessageAsync(root, old.Sha, token);
        await Task.WhenAll(files, message).ConfigureAwait(false);
        return new(message.Result, null, files.Result);
    }

    private string ReadCommitMessage(Repository repo, Commit commit, CancellationToken token)
    {
        var key = new MessageKey(repo.Info.Path, commit.Sha);
        token.ThrowIfCancellationRequested();
        if (_commitMessages.TryGet(key, out var cached)) return cached;
        var message = commit.Message;
        token.ThrowIfCancellationRequested();
        _commitMessages.Put(key, message, 256L + 2L * (key.Repository.Length + key.Commit.Length + message.Length));
        return message;
    }

    private IReadOnlyList<ChangeEntry> ReadTreeChanges(Repository repo, Tree? before, Tree after, ComparisonKind kind, CancellationToken token)
    {
        // Fixed tree-to-tree options: literal paths/status only, no rename
        // detection, workdir, patch, attributes or text conversion. Resolve the
        // actual first-parent tree before this call (also for shallow history).
        var key = new ChangesKey(repo.Info.Path, before?.Sha, after.Sha, kind);
        token.ThrowIfCancellationRequested();
        if (_commitChanges.TryGet(key, out var cached)) return cached;
        using var changes = repo.Diff.Compare<TreeChanges>(before, after, new CompareOptions { Similarity = SimilarityOptions.None });
        var entries = new ChangeEntry[changes.Count];
        var bytes = 384L + 2L * (key.Repository.Length + (key.BeforeTree?.Length ?? 0) + key.AfterTree.Length);
        var index = 0;
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            var path = change.Path; var oldPath = change.OldPath; var status = change.Status.ToString();
            // Literal tree changes normally have the same old/new path. Share
            // that immutable string within this result, without an intern pool.
            if (string.Equals(path, oldPath, StringComparison.Ordinal)) oldPath = path;
            entries[index++] = new(path, kind, status, oldPath);
            bytes += 128L + 2L * (path.Length + status.Length + (ReferenceEquals(path, oldPath) ? 0 : oldPath?.Length ?? 0));
        }
        token.ThrowIfCancellationRequested();
        var result = Array.AsReadOnly(entries);
        _commitChanges.Put(key, result, bytes);
        return result;
    }
}
