using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

public static class RepositoryPatch
{
    public static Task<string> ReadAsync(string root, IEnumerable<string> selected, CancellationToken token = default) => Task.Run(async () =>
    {
        var paths = selected.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new IOException("请选择文件。");
        var state = RepositoryState.Token(root, paths, token);
        using var repo = new Repository(root);
        if (repo.Index.Conflicts.Any()) throw new IOException("请先解决冲突，再导出补丁。");
        var patch = new StringBuilder();
        if (repo.Head.Tip is { } head)
            patch.Append(await GitReadProcess.RunAsync(root, new[] { "diff", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", head.Sha, "--" }.Concat(paths), token));
        foreach (var path in paths.Where(path => repo.Head.Tip is null || repo.Index[path] is null && repo.Head.Tip.Tree[path] is null))
        {
            if (!File.Exists(RepositoryState.CheckedPath(root, path))) continue;
            patch.Append(await GitReadProcess.RunAsync(root, ["diff", "--no-index", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--", "/dev/null", path], token, allowDifferences: true));
        }
        if (state != RepositoryState.Token(root, paths, token)) throw new IOException("文件或仓库已变化，请重新生成补丁。");
        if (patch.Length == 0) throw new IOException("所选文件没有可导出的改动。");
        return patch.ToString();
    }, token);
}
