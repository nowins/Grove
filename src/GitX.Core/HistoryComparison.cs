using LibGit2Sharp;

namespace GitX.Core;

public sealed partial class RepositoryReader
{
    public async Task<IReadOnlyList<ChangeEntry>> CompareChangesAsync(string root, string oldCommit, string? newCommit, CancellationToken token = default)
    {
        if (newCommit is not null) return await ReadWork.Cpu.Run<IReadOnlyList<ChangeEntry>>(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var repo = new Repository(root);
            var old = repo.Lookup<Commit>(oldCommit) ?? throw new IOException("比较起点已不存在。");
            var next = repo.Lookup<Commit>(newCommit) ?? throw new IOException("比较终点已不存在。");
            return ReadTreeChanges(repo, old.Tree, next.Tree, ComparisonKind.RevisionRange, token);
        }, token);

        var sha = await ReadWork.Cpu.Run(() => { using var repo = new Repository(root); return repo.Lookup<Commit>(oldCommit)?.Sha ?? throw new IOException("比较提交已不存在。"); }, token);
        var tracked = await GitReadProcess.RunAsync(root, ["diff", "--name-status", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", sha, "--"], token);
        var untracked = await GitReadProcess.RunAsync(root, ["ls-files", "--others", "--exclude-standard", "-z"], token);
        return await ReadWork.Cpu.Run<IReadOnlyList<ChangeEntry>>(() =>
        {
            using var repo = new Repository(root);
            var tree = repo.Lookup<Commit>(sha)!.Tree;
            var entries = new Dictionary<string, ChangeEntry>(StringComparer.Ordinal);
            var fields = tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 1 < fields.Length; i += 2)
            {
                token.ThrowIfCancellationRequested();
                var status = fields[i] switch { "A" => "Added", "D" => "Deleted", "T" => "TypeChanged", "U" => "Conflicted", _ => "Modified" };
                entries[fields[i + 1]] = new(fields[i + 1], ComparisonKind.CommitWorktree, status);
            }
            foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();
                entries[path] = new(path, ComparisonKind.CommitWorktree, tree[path] is null ? "Added" : "Modified");
            }
            return entries.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        }, token);
    }
}
