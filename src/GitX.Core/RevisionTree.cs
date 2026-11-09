using LibGit2Sharp;

namespace GitX.Core;

public sealed record RevisionTreeEntry(string Path, string Name, bool Directory, bool Submodule, bool SymbolicLink = false);
public sealed record RevisionTreePage(string Sha, string Path, IReadOnlyList<RevisionTreeEntry> Entries, bool HasMore);

public static class RevisionTree
{
    public const int PageSize = 500;
    public static Task<RevisionTreePage> ReadAsync(string root, string sha, string path = "", int offset = 0, CancellationToken token = default)
        => ReadWork.Cpu.Run(() =>
        {
            if (offset < 0 || offset > int.MaxValue - PageSize - 1) throw new IOException("目录分页位置无效。");
            if (path.Length > 0 && (path.Contains('\\') || path.Contains('\0') || path.Split('/').Any(p => p is "" or "." or "..")))
                throw new IOException("无效的仓库目录路径。");
            using var repo = new Repository(root);
            sha = RepositoryActions.CommitSha(root, sha);
            var commit = repo.Lookup<Commit>(sha);
            var tree = path.Length == 0 ? commit.Tree : commit.Tree[path]?.Target as Tree;
            if (tree is null) throw new IOException("此修订中不存在该目录。");
            // Enumerate directory metadata only. Blobs, symlink targets and submodule repositories are never opened here.
            var entries = new List<RevisionTreeEntry>();
            foreach (var entry in tree)
            {
                token.ThrowIfCancellationRequested();
                entries.Add(new(path.Length == 0 ? entry.Name : path + "/" + entry.Name, entry.Name,
                    entry.TargetType == TreeEntryTargetType.Tree, entry.TargetType == TreeEntryTargetType.GitLink, entry.Mode == Mode.SymbolicLink));
            }
            var page = entries.OrderBy(e => !e.Directory).ThenBy(e => e.Name, StringComparer.Ordinal).Skip(offset).Take(PageSize + 1).ToArray();
            return new RevisionTreePage(sha, path, Array.AsReadOnly(page.Take(PageSize).ToArray()), page.Length > PageSize);
        }, token);
}
