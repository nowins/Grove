using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using LibGit2Sharp;

namespace GitX.Core;

public sealed record WorkingTextEditInfo(string RootKey, string Fingerprint, bool Bom, string Newline, bool CanEdit, string? Reason = null);
internal sealed record WorkingFileStamp(long Length, long Created, long Modified, FileAttributes Attributes, int? Mode);
internal sealed record WorkingTextCapture(byte[] Bytes, WorkingFileStamp Stamp, WorkingTextEditInfo Info, string? Text, string? Problem);
internal sealed class WorkingTextLimitException(string message) : IOException(message);

public static class WorkingTextFiles
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    public static string RootKey(string root)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(path))).ToLowerInvariant();
    }
    internal static WorkingTextCapture Capture(Repository repository, string relative, CancellationToken cancellation = default)
    {
        var root = repository.Info.WorkingDirectory;
        var full = RepositoryState.CheckedPath(root, relative);
        var before = Stamp(full); var bytes = ReadBytes(full, cancellation); var after = Stamp(full);
        if (before != after || bytes.LongLength != after.Length) throw new IOException("读取期间文件发生变化，请重试；未创建编辑基线。");
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        string? text = null, reason = null, problem = null;
        try { text = Utf8.GetString(bytes.AsSpan(bom ? 3 : 0)); if (text.Contains('\0')) reason = problem = "二进制或 UTF-16 文件，首版仅支持 UTF-8 文本。"; }
        catch (DecoderFallbackException) { reason = problem = "文件不是有效 UTF-8，首版未提供编码转换。"; }
        var newline = "LF";
        if (text is not null)
        {
            var withoutPairs = text.Replace("\r\n", "");
            var pairs = text.Contains("\r\n");
            if (withoutPairs.Contains('\r') || pairs && withoutPairs.Contains('\n')) reason = "混合或单独 CR 换行暂只读，避免保存时改变行尾。";
            if (pairs) newline = "CRLF";
            if (HasLongLine(text)) reason = problem = "单行超过 20,000 字符，首版暂停编辑器加载。";
        }
        if (after.Attributes.HasFlag(FileAttributes.ReadOnly)) reason = "文件为只读，未启用编辑。";
        if (after.Attributes.HasFlag(FileAttributes.Encrypted)) reason = "加密文件继续只读，避免改变加密属性。";
        if (repository.Index.Conflicts.Any(conflict => conflict.Ancestor?.Path == relative || conflict.Ours?.Path == relative || conflict.Theirs?.Path == relative))
            reason = "冲突文件请在合并窗口处理。";
        var rootKey = RootKey(root);
        var fullIdentity = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        var proof = JsonSerializer.SerializeToUtf8Bytes(new { rootKey, fullIdentity, after, head = Head(repository), raw = Hash(bytes) });
        return new(bytes, after, new(rootKey, Hash(proof), bom, newline, reason is null, reason), text, problem);
    }
    internal static string Head(Repository repository) => repository.Head.CanonicalName + "\n" + repository.Head.Tip?.Sha;
    internal static WorkingFileStamp Stamp(string full)
    {
        var info = new FileInfo(full); info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("文件已被删除，草稿仍保留；不会重新创建或覆盖其他文件。");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("文件已变为链接，未执行保存。");
        return new(info.Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, info.Attributes,
            OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(full));
    }
    internal static byte[] ReadBytes(string full, CancellationToken cancellation = default)
    {
        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > RepositoryReader.MaxTextBytes) throw new WorkingTextLimitException("文件超过 1 MiB，暂停全文编辑。");
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (output.Length + count > RepositoryReader.MaxTextBytes) throw new WorkingTextLimitException("文件超过 1 MiB，暂停全文编辑。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    internal static bool HasLongLine(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n')
        { if (i - start > 20000) return true; start = i + 1; }
        return text.Length - start > 20000;
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static byte[] Encode(string text, WorkingTextEditInfo baseline)
    {
        if (text.Contains('\0')) throw new IOException("内容含 NUL，未保存二进制文本。");
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.Contains('\r')) throw new IOException("内容含单独 CR，未改变换行格式。");
        if (HasLongLine(normalized)) throw new IOException("单行超过 20,000 字符，草稿保留但未保存。");
        if (baseline.Newline == "CRLF") normalized = normalized.Replace("\n", "\r\n");
        byte[] bytes;
        try { bytes = Utf8.GetBytes((baseline.Bom ? "\uFEFF" : "") + normalized); }
        catch (EncoderFallbackException error) { throw new IOException("内容包含无效 Unicode，草稿保留但未保存。", error); }
        if (bytes.Length > RepositoryReader.MaxTextBytes) throw new IOException("保存内容超过 1 MiB，草稿保留但未写入。");
        return bytes;
    }
}

public sealed class WorkingTextWriter
{
    private readonly Func<Task>? _beforeInstall, _afterInstall;
    public WorkingTextWriter() { }
    internal WorkingTextWriter(Func<Task>? beforeInstall, Func<Task>? afterInstall = null) { _beforeInstall = beforeInstall; _afterInstall = afterInstall; }
    public Task<WorkingTextEditInfo> SaveAsync(string root, string path, string expected, string text) => RepositoryWriter.Locked(root, async () =>
    {
        WorkingTextCapture ReadCurrent() { using var repo = new Repository(root); return WorkingTextFiles.Capture(repo, path); }
        var initial = ReadCurrent();
        if (initial.Info.Fingerprint != expected) throw new IOException("磁盘文件或版本已变化，未覆盖；草稿仍保留。");
        if (!initial.Info.CanEdit) throw new IOException(initial.Info.Reason ?? "当前文件只读。");
        var bytes = WorkingTextFiles.Encode(text, initial.Info);
        if (bytes.AsSpan().SequenceEqual(initial.Bytes)) return initial.Info;
        var full = RepositoryState.CheckedPath(root, path);
        string gitDirectory, originalHead;
        using (var repo = new Repository(root)) { gitDirectory = repo.Info.Path; originalHead = WorkingTextFiles.Head(repo); }
        var directory = PrivateDirectory(Path.GetDirectoryName(full)!, ".grove-save-");
        string pending = Path.Combine(directory, "pending"), backup = Path.Combine(directory, "original"), raced = Path.Combine(directory, "raced");
        var installed = false; string? recovery = null;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(pending, options)) { await output.WriteAsync(bytes); output.Flush(true); }
            if (!OperatingSystem.IsWindows() && initial.Stamp.Mode is { } mode) File.SetUnixFileMode(pending, (UnixFileMode)mode);
            if (OperatingSystem.IsWindows()) File.SetAttributes(pending, initial.Stamp.Attributes & (FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed));
            if (_beforeInstall is not null) await _beforeInstall();
            var current = ReadCurrent();
            if (!current.Info.CanEdit || current.Info.Fingerprint != expected) throw new IOException("保存前磁盘文件或权限发生变化，未覆盖；草稿仍保留。");
            RepositoryState.CheckedPath(root, path);
            File.Replace(pending, full, backup, ignoreMetadataErrors: false); installed = true;
            if (_afterInstall is not null) await _afterInstall();
            if (File.GetAttributes(backup).HasFlag(FileAttributes.ReparsePoint) || WorkingTextFiles.Hash(WorkingTextFiles.ReadBytes(backup)) != WorkingTextFiles.Hash(initial.Bytes))
                throw new IOException("检测到原子替换期间的外部修改，正在保留冲突副本。");
            var saved = ReadCurrent();
            if (!saved.Bytes.AsSpan().SequenceEqual(bytes)) throw new IOException("保存后文件又被外部程序修改，未将草稿标记为已保存。");
            using (var verified = new Repository(root))
                if (WorkingTextFiles.Head(verified) != originalHead)
                    throw new IOException("保存期间分支或 HEAD 发生变化，未确认保存成功；请核对工作区和恢复副本。");
            File.Delete(backup); return saved.Info;
        }
        catch (Exception error)
        {
            // The backup is the file actually replaced, not merely an earlier
            // read. Restore it only while the destination is still our bytes
            // and the repository context is unchanged. Keep uncertain data.
            if ((installed || File.Exists(backup)) && File.Exists(backup) && !File.GetAttributes(backup).HasFlag(FileAttributes.ReparsePoint))
            {
                try
                {
                    using var repo = new Repository(root);
                    RepositoryState.CheckedPath(root, path);
                    if (WorkingTextFiles.Head(repo) == originalHead && File.Exists(full) && WorkingTextFiles.Hash(WorkingTextFiles.ReadBytes(full)) == WorkingTextFiles.Hash(bytes))
                        File.Replace(backup, full, raced, ignoreMetadataErrors: false);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { }
            }
            try
            {
                recovery = PrivateDirectory(Path.Combine(gitDirectory, "grove-text-recovery"), "save-");
                foreach (var candidate in new[] { pending, backup, raced })
                    if (File.Exists(candidate) || new FileInfo(candidate).LinkTarget is not null)
                    {
                        var destination = Path.Combine(recovery, Path.GetFileName(candidate));
                        var link = new FileInfo(candidate).LinkTarget;
                        if (link is not null) { await File.WriteAllTextAsync(destination + ".link.txt", link); File.Delete(candidate); }
                        else File.Move(candidate, destination);
                    }
                await File.WriteAllBytesAsync(Path.Combine(recovery, "draft"), bytes);
                await File.WriteAllTextAsync(Path.Combine(recovery, "target.json"), JsonSerializer.Serialize(new { path, expected, error = error.Message }));
            }
            catch (Exception preserve) when (preserve is IOException or UnauthorizedAccessException) { recovery = recovery is null ? directory : recovery + "；仍可能保留的暂存目录：" + directory; }
            throw new IOException("未完成保存，草稿仍保留。" + error.Message + " 恢复副本：" + recovery, error);
        }
        finally
        {
            // No recursive deletion or removal of unknown contents. A failed
            // recovery stays available at the reported private location.
            try { Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });
    internal static string PrivateDirectory(string parent, string prefix)
    {
        void CheckParents()
        {
            for (DirectoryInfo? current = new(parent); current is not null; current = current.Parent)
                if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("恢复或临时目录包含链接，未写入该位置。");
        }
        CheckParents(); Directory.CreateDirectory(parent); CheckParents();
        var directory = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory); RestrictWindows(directory);
        }
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }
    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string directory)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法确定保存临时目录的所有者。");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
