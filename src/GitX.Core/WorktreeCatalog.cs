namespace GitX.Core;

public sealed record WorktreeEntry(string Root, string? Head, string? Branch, bool Detached, bool Bare,
    string? LockedReason, string? PrunableReason, bool Current, bool Missing)
{
    public string BranchLabel => Bare ? "裸仓库" : Detached ? "分离 HEAD" + (Head is { Length: >= 8 } ? " · " + Head[..8] : "") :
        Branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? Branch[11..] : Branch ?? "尚无分支";
    public string? UnavailableReason => Bare ? "裸仓库没有工作区" : Missing ? "工作树目录不存在" :
        PrunableReason is not null ? "工作树登记失效：" + PrunableReason : null;
    public string AccessibleName => $"{BranchLabel}，{(Current ? "当前工作树，" : "")}{(LockedReason is not null ? "已锁定，" : "")}{UnavailableReason ?? "可打开"}，路径 {Root}，提交 {Head ?? "尚无提交"}";
}

public sealed record WorktreeSnapshot(string Root, string CommonDirectory, IReadOnlyList<WorktreeEntry> Entries);

// NUL-delimited porcelain preserves spaces, Unicode, quotes and newlines in paths/reasons.
// Discovery and switching never create, remove, prune or unlock a worktree.
public static class WorktreeCatalog
{
    internal static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static async Task<WorktreeSnapshot> ReadAsync(string root, CancellationToken cancellation = default)
    {
        var common = await GitReadProcess.RunAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellation);
        var output = await GitReadProcess.RunAsync(root, ["worktree", "list", "--porcelain", "-z"], cancellation);
        return new(root, Path.GetFullPath(common.TrimEnd('\r', '\n')), Parse(output, root));
    }

    internal static IReadOnlyList<WorktreeEntry> Parse(string output, string root)
    {
        var entries = new List<WorktreeEntry>();
        string? path = null, head = null, branch = null, locked = null, prunable = null;
        bool detached = false, bare = false;
        foreach (var field in output.Split('\0').Append(""))
        {
            if (field.Length == 0)
            {
                if (path is null) continue;
                entries.Add(new(path, head, branch, detached, bare, locked, prunable, SamePath(root, path), !Directory.Exists(path)));
                path = head = branch = locked = prunable = null; detached = bare = false;
                continue;
            }
            var separator = field.IndexOf(' ');
            var key = separator < 0 ? field : field[..separator];
            var value = separator < 0 ? "" : field[(separator + 1)..];
            switch (key)
            {
                case "worktree": path = value; break;
                case "HEAD": head = value; break;
                case "branch": branch = value; break;
                case "detached": detached = true; break;
                case "bare": bare = true; break;
                case "locked": locked = value; break;
                case "prunable": prunable = value; break;
            }
        }
        return entries;
    }

    public static async Task<string> ResolveOpenAsync(WorktreeSnapshot snapshot, WorktreeEntry selected, CancellationToken cancellation = default)
    {
        if (!snapshot.Entries.Contains(selected)) throw new IOException("工作树不属于当前列表，请重新打开工作树选择器。");
        var current = await ReadAsync(snapshot.Root, cancellation);
        if (!SamePath(current.CommonDirectory, snapshot.CommonDirectory)) throw new IOException("原仓库已变化，请重新打开工作树选择器。");
        var target = current.Entries.SingleOrDefault(e => SamePath(e.Root, selected.Root))
            ?? throw new IOException("工作树已被移除或移动，请刷新列表。");
        if (target.UnavailableReason is { } reason) throw new IOException(reason);
        var resolved = await RepositoryReader.ResolveWorkingDirectoryAsync(target.Root);
        if (!SamePath(resolved, target.Root)) throw new IOException("目标目录已不再是该工作树，请刷新列表。");
        var targetCommon = await GitReadProcess.RunAsync(resolved, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellation);
        if (!SamePath(targetCommon.TrimEnd('\r', '\n'), snapshot.CommonDirectory)) throw new IOException("目标路径已属于其他仓库，未切换工作树。");
        cancellation.ThrowIfCancellationRequested();
        return resolved;
    }
}
