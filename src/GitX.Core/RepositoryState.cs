using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

public static class RepositoryState
{
    public static string Token(string root, IEnumerable<string> paths, CancellationToken token = default)
    {
        using var repo = new Repository(root);
        var state = new StringBuilder().Append(repo.Head.CanonicalName).Append('|').Append(repo.Head.Tip?.Sha);
        var objectIdBytes = repo.Config.Get<string>("extensions.objectFormat")?.Value == "sha256" ? 32 : 20;
        state.Append('|').Append(IndexFingerprint.Read(Path.Combine(repo.Info.Path, "index"), objectIdBytes, token));
        foreach (var path in RepositoryOperation.StateFiles) state.Append('|').Append(HashFile(Path.Combine(repo.Info.Path, path), token));
        foreach (var path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            state.Append('|').Append(path).Append(':').Append(HashFile(CheckedPath(root, path), token));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
    }

    public static string CheckedPath(string root, string path)
    {
        if (path.Split('/', '\\').Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase))) throw new IOException("不能操作 Git 元数据路径。");
        var full = RepositoryReader.SafeWorktreePath(root, path);
        for (FileSystemInfo? cursor = new FileInfo(full); cursor is not null; cursor = Directory.GetParent(cursor.FullName))
            if (cursor.Exists && cursor.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("暂不写入符号链接或目录联接。");
        if (Directory.Exists(full)) throw new IOException("请选择文件，不能以目录作为写操作目标。");
        return full;
    }

    private static string HashFile(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return "missing";
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int count;
        while ((count = input.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
