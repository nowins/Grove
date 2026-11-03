namespace GitX.Core;

public sealed record WorktreeRemovalPlan(WorktreeSnapshot Snapshot, WorktreeEntry Target, string GitDirectory);

// Removal is deliberately conservative: no force, prune, branch deletion, recursive
// file deletion, or cancellation of a Git write. Only Git removes its own worktree.
public static class WorktreeRemoval
{
    public static async Task<WorktreeRemovalPlan> PrepareAsync(WorktreeSnapshot snapshot, WorktreeEntry selected, CancellationToken cancellation = default)
    {
        if (!snapshot.Entries.Contains(selected)) throw new IOException("工作树不属于当前列表，请刷新。");
        var current = await WorktreeCatalog.ReadAsync(snapshot.Root, cancellation);
        if (!WorktreeCatalog.SamePath(current.CommonDirectory, snapshot.CommonDirectory)) throw new IOException("源仓库已变化，未删除工作树。");
        var target = current.Entries.SingleOrDefault(e => WorktreeCatalog.SamePath(e.Root, selected.Root))
            ?? throw new IOException("工作树已被移除或移动，请刷新。");
        if (target.Current || WorktreeCatalog.SamePath(snapshot.Root, target.Root)) throw new IOException("不能删除当前工作树。请先打开其他工作树。");
        if (target.Bare || target.UnavailableReason is not null) throw new IOException("不能删除裸仓库、缺失或失效的工作树；此入口不会清理失效记录。");
        if (target.LockedReason is not null) throw new IOException("工作树已锁定，未删除。请先在外部核查锁定原因。");
        if (target.Head != selected.Head || target.Branch != selected.Branch || target.Detached != selected.Detached)
            throw new IOException("目标工作树的分支或提交已变化，请重新确认。");
        RejectLinks(target.Root);
        var root = await WorktreeCatalog.ResolveOpenAsync(current, target, cancellation);
        var gitDirectory = (await GitReadProcess.RunAsync(root, ["rev-parse", "--absolute-git-dir"], cancellation)).TrimEnd('\r', '\n');
        if (WorktreeCatalog.SamePath(gitDirectory, snapshot.CommonDirectory) || Directory.Exists(Path.Combine(root, ".git")))
            throw new IOException("不能删除主工作树。");
        RejectLinks(gitDirectory);
        if (!File.Exists(Path.Combine(root, ".git"))) throw new IOException("目标不再是已登记的关联工作树。");
        // Include ignored files: Git may otherwise delete ignored local data even
        // without --force. Never offer a force override from this interface.
        var status = await GitReadProcess.RunAsync(root, ["-c", "core.fsmonitor=false", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching"], cancellation);
        if (status.Length != 0) throw new IOException("工作树包含未提交、未跟踪或被忽略的本地文件，未删除。请先在该工作树中保存或自行整理文件。");
        var files = await GitReadProcess.RunAsync(root, ["ls-files", "--stage", "-z"], cancellation);
        if (files.Split('\0').Any(f => f.StartsWith("160000 ", StringComparison.Ordinal))) throw new IOException("工作树包含子模块，当前不支持删除。请使用 Git 手动核查处理。");
        var flags = await GitReadProcess.RunAsync(root, ["ls-files", "-v", "-z"], cancellation);
        if (flags.Split('\0').Any(f => f.Length > 0 && (f[0] == 'S' || char.IsLower(f[0]))))
            throw new IOException("工作树存在 skip-worktree 或 assume-unchanged 文件，无法可靠检查本地改动，未删除。");
        foreach (var name in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG", "rebase-merge", "rebase-apply", "sequencer", "index.lock", "HEAD.lock" })
            if (Path.Exists(Path.Combine(gitDirectory, name))) throw new IOException("目标工作树存在进行中的 Git 操作或锁文件，未删除。");
        cancellation.ThrowIfCancellationRequested();
        return new(current, target, Path.GetFullPath(gitDirectory));
    }

    public static Task<string> RemoveAsync(WorktreeRemovalPlan plan)
    {
        // Use stable ordering for source/target gates, including removal requests
        // originating in different worktrees of the same repository.
        var paths = new[] { plan.Snapshot.Root, plan.Target.Root, plan.Snapshot.CommonDirectory }
            .Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        return LockAsync(0);
        Task<string> LockAsync(int index) => index < paths.Length ? RepositoryWriter.Locked(paths[index], () => LockAsync(index + 1)) : RemoveCheckedAsync();
        async Task<string> RemoveCheckedAsync()
        {
            var fresh = await PrepareAsync(plan.Snapshot, plan.Target);
            if (!WorktreeCatalog.SamePath(fresh.GitDirectory, plan.GitDirectory)) throw new IOException("目标工作树登记已变化，请重新确认。");
            var result = await GitWriteProcess.RepositoryAsync(fresh.Snapshot.Root, ["worktree", "remove", "--", fresh.Target.Root]);
            var after = await WorktreeCatalog.ReadAsync(fresh.Snapshot.Root);
            if (after.Entries.Any(e => WorktreeCatalog.SamePath(e.Root, fresh.Target.Root)) || Directory.Exists(fresh.Target.Root))
                throw new IOException("Git 已返回，但工作树或目录仍存在。请刷新并核查 Git 输出。");
            return $"已删除关联工作树：{fresh.Target.Root}\n分支与提交保留。\n{result}";
        }
    }

    private static void RejectLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("工作树或登记目录是符号链接或目录联接，未删除。");
        var gitFile = Path.Combine(path, ".git");
        if (File.Exists(gitFile) && (File.GetAttributes(gitFile) & FileAttributes.ReparsePoint) != 0) throw new IOException("工作树登记文件是符号链接，未删除。");
    }
}
