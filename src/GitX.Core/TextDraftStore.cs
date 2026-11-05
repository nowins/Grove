using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibGit2Sharp;

namespace GitX.Core;

public sealed record TextDraftRecord(int Format, string Repository, string Path, string Revision,
    DateTimeOffset Updated, WorkingTextEditInfo Baseline, string Text);
public sealed record TextDraftListing(IReadOnlyList<TextDraftRecord> Drafts, int Unreadable, int Expired, int Unassigned = 0);

// Local, opt-in storage. This class never writes repository files. No shared index:
// a corrupt record cannot hide the other drafts. Callers must retain the revision
// token until explicit discard/save, and never acknowledge a failed checkpoint.
public sealed class TextDraftStore
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxStoreBytes = 16 * 1024 * 1024;
    public const int MaxRecords = 64;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int MaxRecordBytes = 8 * MaxTextBytes;
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action? _beforeInstall;
    public TextDraftStore(string directory) : this(directory, () => DateTimeOffset.UtcNow, null) { }
    internal TextDraftStore(string directory, Func<DateTimeOffset> now, Action? beforeInstall)
    { _directory = Path.GetFullPath(directory); _now = now; _beforeInstall = beforeInstall; }

    public bool Enabled
    {
        get { using var gate = Lock(); var file = Path.Combine(_directory, "enabled"); if (!File.Exists(file)) return false; SafeLength(file); return true; }
    }
    public void SetEnabled(bool enabled)
    {
        using var gate = Lock(); var file = Path.Combine(_directory, "enabled");
        if (File.Exists(file)) SafeLength(file);
        if (enabled) { using var output = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
        else File.Delete(file);
    }

    public static bool IsExcluded(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.Equals(".ssh", StringComparison.OrdinalIgnoreCase)) ||
            parts.Any(p => p.Equals(".env", StringComparison.OrdinalIgnoreCase) || p.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) ||
            new[] { ".pem", ".key", ".p12", ".pfx", ".keystore" }.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ||
            new[] { "id_rsa", "id_ed25519", "credentials", "credentials.json", "secrets.json" }.Contains(parts.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public static string RepositoryIdentity(string root)
    {
        using var repository = new Repository(root);
        var work = repository.Info.WorkingDirectory ?? throw new IOException("草稿恢复仅支持工作区仓库。");
        CheckParents(work);
        var git = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.Info.Path));
        CheckParents(git);
        // Creation identity avoids silently assigning drafts to a newly initialized
        // repository at the same path. Worktrees retain their own working-root key.
        return WorkingTextFiles.Hash(WorkingTextFiles.Utf8.GetBytes(WorkingTextFiles.RootKey(work) + "\n" + git + "\n" + DirectoryIdentity(git)));
    }

    private static string DirectoryIdentity(string path)
    {
        if (!OperatingSystem.IsLinux()) return Directory.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // On Linux, .NET creation time may fall back to mutable ctime/mtime.
        // statx has a fixed ABI on x64/arm64; inode + device remain stable across
        // Git's atomic config/ref replacements. Use birth time only when supplied.
        if (Statx(-100, path, 0x100, 0x7ff | 0x800, out var stat) != 0 || (stat.Mask & 0x100) == 0)
            throw new IOException("无法验证仓库目录身份，未访问恢复草稿。", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        if ((stat.Mask & 0x800) == 0) throw new IOException("当前文件系统不提供稳定的仓库创建身份，未访问恢复草稿；请保留编辑器并正常保存文件。");
        var birth = $"{stat.BirthSeconds}:{stat.BirthNanoseconds}";
        return $"linux:{stat.DeviceMajor}:{stat.DeviceMinor}:{stat.Inode}:{birth}";
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx result);

    public TextDraftRecord Put(string root, string path, WorkingTextEditInfo baseline, string text, string? expectedRevision)
    {
        ValidatePath(path);
        if (IsExcluded(path)) throw new IOException("此路径被敏感文件排除规则阻止，草稿只保留在内存中。");
        if (!baseline.CanEdit || baseline.RootKey != WorkingTextFiles.RootKey(root)) throw new IOException("草稿仓库身份不匹配。");
        if (WorkingTextFiles.Utf8.GetByteCount(text) > MaxTextBytes || text.Contains('\0')) throw new IOException("草稿超过 1 MiB 或含二进制内容，未持久化。");
        var repository = RepositoryIdentity(root);
        using var gate = Lock();
        if (!File.Exists(Path.Combine(_directory, "enabled"))) throw new IOException("本地草稿恢复已关闭。");
        var file = RecordPath(repository, path);
        var previous = File.Exists(file) ? Read(file) : null;
        if (previous?.Revision != expectedRevision) throw new IOException("恢复草稿已变化，请先审阅现有副本。");
        var record = new TextDraftRecord(1, repository, path, Guid.NewGuid().ToString("N"), _now(), baseline, text);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        var all = Directory.GetFiles(_directory).Where(p => Path.GetExtension(p) is ".json" or ".pending").ToArray();
        if (all.Length >= MaxRecords && previous is null || all.Sum(p => SafeLength(p)) - (previous is null ? 0 : SafeLength(file)) + bytes.Length > MaxStoreBytes)
            throw new IOException("草稿恢复容量已满；请审阅或清理已有草稿。不会自动驱逐未保存内容。");
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options)) { output.Write(bytes); output.Flush(true); }
            _beforeInstall?.Invoke();
            CheckParents(_directory);
            File.Move(temporary, file, overwrite: true);
            return record;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public TextDraftListing List(string root)
    {
        var repository = RepositoryIdentity(root);
        using var gate = Lock();
        var drafts = new List<TextDraftRecord>(); int unreadable = 0, expired = 0, unassigned = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var record = Read(file);
                if (record.Repository != repository) { if (record.Baseline.RootKey == WorkingTextFiles.RootKey(root)) unassigned++; continue; }
                if (_now() - record.Updated > Retention) { expired++; continue; }
                drafts.Add(record);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { unreadable++; }
        }
        return new(drafts.OrderByDescending(d => d.Updated).ToArray(), unreadable, expired, unassigned);
    }

    // Only the precise record reviewed by the user can be removed. A delayed
    // clean/save notification cannot delete a newer draft from another session.
    public bool Contains(string root, TextDraftRecord expected, bool includeExpired = false)
    {
        if (RepositoryIdentity(root) != expected.Repository) return false;
        using var gate = Lock();
        var file = RecordPath(expected.Repository, expected.Path);
        return (includeExpired || _now() - expected.Updated <= Retention) && File.Exists(file) && Read(file) == expected;
    }
    public void Discard(string root, TextDraftRecord expected)
    {
        if (RepositoryIdentity(root) != expected.Repository) throw new IOException("仓库身份已变化，未清理恢复草稿。");
        using var gate = Lock();
        var file = RecordPath(expected.Repository, expected.Path);
        if (!File.Exists(file)) return;
        if (Read(file).Revision != expected.Revision) throw new IOException("恢复草稿已变化，未清理新版。");
        File.Delete(file);
    }

    // Explicit UI action, not an automatic cleanup on startup. Includes expired
    // and corrupt records, but never follows links or recursively removes data.
    public void Clear()
    {
        using var gate = Lock();
        foreach (var file in Directory.GetFiles(_directory))
            if (Path.GetExtension(file) is ".json" or ".pending") { SafeLength(file); File.Delete(file); }
    }

    private FileStream Lock()
    {
        CheckParents(_directory);
        if (!Directory.Exists(_directory))
        {
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(_directory); WorkingTextWriter.RestrictWindows(_directory); }
            else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        CheckParents(_directory);
        if (OperatingSystem.IsWindows()) CheckWindowsAccess(new DirectoryInfo(_directory));
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(_directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("草稿目录权限不是仅当前用户可读，未访问草稿。");
        var file = Path.Combine(_directory, "store.lock");
        if (File.Exists(file)) SafeLength(file);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(file, options);
    }
    private TextDraftRecord Read(string file)
    {
        if (SafeLength(file) > MaxRecordBytes) throw new IOException("恢复记录过大。");
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Bound allocation even if an external writer grows a record after stat.
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer)) > 0) { if (output.Length + count > MaxRecordBytes) throw new IOException("恢复记录过大。"); output.Write(buffer, 0, count); }
        var record = JsonSerializer.Deserialize<TextDraftRecord>(output.ToArray()) ?? throw new IOException("恢复记录为空。");
        ValidatePath(record.Path);
        if (record.Format != 1 || !IsHash(record.Repository) || record.Revision?.Length != 32 || record.Baseline is null || !IsHash(record.Baseline.RootKey) ||
            !IsHash(record.Baseline.Fingerprint) || record.Text is null || WorkingTextFiles.Utf8.GetByteCount(record.Text) > MaxTextBytes || record.Text.Contains('\0') ||
            Path.GetFileName(file) != Path.GetFileName(RecordPath(record.Repository, record.Path))) throw new IOException("恢复记录无效。");
        return record;
    }
    private string RecordPath(string repository, string path) => Path.Combine(_directory, WorkingTextFiles.Hash(WorkingTextFiles.Utf8.GetBytes(repository + "\n" + path)) + ".json");
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or "..")) throw new IOException("草稿路径无效。");
    }
    private static long SafeLength(string file)
    {
        var info = new FileInfo(file);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("恢复记录包含链接，未访问。");
        if (OperatingSystem.IsWindows()) CheckWindowsAccess(info);
        return info.Length;
    }
    [SupportedOSPlatform("windows")]
    private static void CheckWindowsAccess(FileSystemInfo item)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法验证恢复目录所有者。");
        FileSystemSecurity security = item is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier))) || item is DirectoryInfo && !security.AreAccessRulesProtected)
            throw new IOException("恢复目录权限不是仅当前用户可读，未访问草稿。");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference))
                throw new IOException("恢复副本权限包含其他主体，未访问草稿。");
    }
    private static void CheckParents(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            if (current.LinkTarget is not null || current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("恢复目录包含链接，未访问。");
    }
}
