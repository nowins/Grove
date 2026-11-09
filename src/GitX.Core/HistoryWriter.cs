using System.Text;
using System.Text.Json;
using LibGit2Sharp;

namespace GitX.Core;

public enum HistoryAction { CherryPick, Revert, ResetSoft, ResetMixed, ResetHard, ResetKeep }
public sealed class HistoryWriter
{
    public Task<string> ExecuteAsync(ActionContext context, HistoryAction action, string sha, int mainline = 0) => RepositoryWriter.Locked(context.Root, async () =>
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        RepositoryActions.Check(context, action is HistoryAction.CherryPick or HistoryAction.Revert);
        sha = RepositoryActions.CommitSha(context.Root, sha);
        if (action is HistoryAction.CherryPick or HistoryAction.Revert)
        {
            using var repo = new Repository(context.Root); var parents = repo.Lookup<Commit>(sha).Parents.Count();
            if (parents > 1 && (mainline < 1 || mainline > parents)) throw new IOException("合并提交必须明确选择主线父提交。");
            var kind = action == HistoryAction.CherryPick ? "cherry-pick" : "revert";
            var args = new List<string> { kind, "--no-edit" };
            if (parents > 1) args.AddRange(["-m", mainline.ToString()]); args.Add(sha);
            return await RepositoryActions.RunWithConflicts(context.Root, kind, args);
        }
        if (context.Head is null) throw new IOException("当前工作树尚无提交，不能执行 Reset。");
        var worktrees = await WorktreeCatalog.ReadAsync(context.Root);
        var current = worktrees.Entries.SingleOrDefault(e => e.Current)
            ?? throw new IOException("当前工作树登记已变化，请重新打开仓库。");
        if (current.Branch is { } branch && worktrees.Entries.Any(e => !e.Current && e.Branch == branch))
            throw new IOException("当前分支同时由其他 worktree 使用。请先在其他工作树切换分支，再执行 Reset。");
        var recovery = await SaveRecoveryAsync(context, action == HistoryAction.ResetHard ? sha : null);
        RepositoryActions.Check(context);
        var mode = action switch { HistoryAction.ResetSoft => "--soft", HistoryAction.ResetMixed => "--mixed",
            HistoryAction.ResetHard => "--hard", HistoryAction.ResetKeep => "--keep", _ => throw new ArgumentOutOfRangeException(nameof(action)) };
        var output = await GitWriteProcess.RepositoryAsync(context.Root, ["reset", mode, sha, "--"]);
        return $"{output}\n恢复记录：{recovery}";
    });

    public Task<string> AmendAsync(ActionContext context, string message) => RepositoryWriter.Locked(context.Root, async () =>
    {
        RepositoryActions.Check(context);
        if (context.Head is null || string.IsNullOrWhiteSpace(message)) throw new IOException("需要已有提交和非空说明。");
        var recovery = await SaveRecoveryAsync(context, null); RepositoryActions.Check(context);
        var output = await GitWriteProcess.RepositoryAsync(context.Root, ["commit", "--amend", "--file=-"], Encoding.UTF8.GetBytes(message));
        return $"{output}\n原提交恢复记录：{recovery}";
    });

    internal static async Task<string> SaveRecoveryAsync(ActionContext context, string? hardTarget)
    {
        using var repo = new Repository(context.Root);
        var directory = Path.Combine(repo.Info.Path, "gitx-recovery", Guid.NewGuid().ToString("N"));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in repo.RetrieveStatus())
            if (status.State is not (FileStatus.Unaltered or FileStatus.Ignored)) paths.Add(status.FilePath);
        if (hardTarget is not null)
        {
            var targetPaths = (await GitReadProcess.RunAsync(context.Root, ["ls-tree", "-r", "--name-only", "-z", hardTarget], default)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in targetPaths)
            {
                var full = Path.Combine(context.Root, path);
                if (Directory.Exists(full)) throw new IOException($"目标文件被目录阻挡：{path}。请先移走该目录，未执行 hard reset。");
                if (repo.Index[path] is null && File.Exists(full)) paths.Add(path);
                var parent = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(parent))
                { if (File.Exists(Path.Combine(context.Root, parent))) paths.Add(parent); parent = Path.GetDirectoryName(parent); }
            }
        }
        foreach (var path in paths) RepositoryState.CheckedPath(context.Root, path);
        var before = RepositoryState.Token(context.Root, paths);
        Directory.CreateDirectory(directory); var manifest = new List<object>(); var i = 0;
        var indexPath = Path.Combine(repo.Info.Path, "index");
        if (File.Exists(indexPath)) File.Copy(indexPath, Path.Combine(directory, "index"));
        foreach (var path in paths)
        {
            var full = RepositoryState.CheckedPath(context.Root, path); var exists = File.Exists(full); var backup = $"{i++}.content";
            if (exists) File.Copy(full, Path.Combine(directory, backup));
            var entry = repo.Index[path];
            if (entry is not null && repo.Lookup<Blob>(entry.Id) is { } blob)
            { using var content = blob.GetContentStream(); using var file = File.Create(Path.Combine(directory, backup + ".index")); await content.CopyToAsync(file); }
            manifest.Add(new { path, exists, backup, indexBlob = entry?.Id.Sha, indexMode = entry?.Mode });
        }
        var reference = "refs/gitx/recovery/" + Path.GetFileName(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { context.Head, reference, files = manifest }, new JsonSerializerOptions { WriteIndented = true }));
        if (before != RepositoryState.Token(context.Root, paths)) throw new IOException("备份期间工作区或索引已变化，未执行历史改写。请重试。");
        if (context.Head is not null) await GitWriteProcess.RepositoryAsync(context.Root, ["update-ref", reference, context.Head]);
        return directory;
    }
}

public enum StashAction { Save, Apply, Pop, Drop }
public sealed class StashWriter
{
    public Task<string> ExecuteAsync(ActionContext context, StashAction action, StashEntry? stash = null, string message = "") => RepositoryWriter.Locked(context.Root, async () =>
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        RepositoryActions.Check(context, action is StashAction.Apply or StashAction.Pop);
        if (action == StashAction.Save)
        {
            if (!context.Dirty || context.Head is null) throw new IOException("需要已有提交和本地改动才能 Stash。");
            return await GitWriteProcess.RepositoryAsync(context.Root, ["stash", "push", "--include-untracked", "-m", string.IsNullOrWhiteSpace(message) ? "Grove stash" : message]);
        }
        if (stash is null || !context.Stashes.Contains(stash)) throw new IOException("Stash 不属于当前快照，请重新选择。");
        var selector = $"stash@{{{stash.Index}}}";
        if (action == StashAction.Drop) return await GitWriteProcess.RepositoryAsync(context.Root, ["stash", "drop", selector]);
        try { return await GitWriteProcess.RepositoryAsync(context.Root, ["stash", action == StashAction.Pop ? "pop" : "apply", "--index", selector]); }
        catch (IOException e)
        {
            using var repo = new Repository(context.Root);
            if (repo.Index.Conflicts.Any()) return $"Stash 应用出现冲突，Stash 已保留。请处理未合并索引，再按需删除 Stash。\n{e.Message}";
            throw;
        }
    });
}
