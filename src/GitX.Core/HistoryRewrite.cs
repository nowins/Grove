using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;

namespace GitX.Core;

public enum RewriteAction { Pick, Reword, Squash, Fixup, Drop }
public sealed record RewriteCommit(string Sha, string Message)
{
    public string Label => Sha[..8] + "  " + Message.Split('\n')[0];
}
public sealed record RewriteStep(string Sha, RewriteAction Action, string Message = "");
public sealed record HistoryRewritePlan(ActionContext Context, string? Base, IReadOnlyList<RewriteCommit> Commits);

public static class HistoryRewrite
{
    public const int MaximumCommits = 100;
    private const string Marker = "grove-history-edit.json";
    private sealed record RewordFile(string Sha, string Digest);
    private sealed record RewordState(string OriginalHead, string RecoveryId, RewordFile[] Files, string CommentCharacter);

    public static async Task<(HistoryRewritePlan Plan, IReadOnlyList<RewriteStep> Steps)> PrepareSelectionAsync(ActionContext context,
        IEnumerable<string> selection, RewriteAction action, CancellationToken cancellation = default)
    {
        var input = selection.ToArray(); var selected = input.ToHashSet(StringComparer.Ordinal);
        if (selected.Count is < 1 or > MaximumCommits || selected.Count != input.Length || action is not (RewriteAction.Drop or RewriteAction.Squash or RewriteAction.Fixup))
            throw new IOException("请选择 1–100 个提交用于 Drop／Squash／Fixup。");
        var combine = action is RewriteAction.Squash or RewriteAction.Fixup;
        var oldest = await Task.Run(() =>
        {
            using var repo = new Repository(context.Root);
            foreach (var sha in selected) RepositoryActions.CommitSha(context.Root, sha);
            var remaining = selected.ToHashSet(StringComparer.Ordinal); var cursor = repo.Head.Tip;
            for (var i = 0; cursor is not null && i < MaximumCommits; i++)
            {
                cancellation.ThrowIfCancellationRequested(); remaining.Remove(cursor.Sha);
                if (remaining.Count == 0)
                {
                    if (!combine || selected.Count > 1) return cursor.Sha;
                    var parents = cursor.Parents.ToArray();
                    if (parents.Length != 1) throw new IOException("此提交没有唯一的上一提交可供合并。");
                    selected.Add(parents[0].Sha); return parents[0].Sha;
                }
                var parent = cursor.Parents.ToArray(); if (parent.Length > 1) break; cursor = parent.FirstOrDefault();
            }
            throw new IOException("所选提交必须位于当前分支最近 100 个提交的线性历史中。");
        }, cancellation);
        var plan = await PrepareAsync(context, oldest, cancellation);
        var indices = plan.Commits.Select((c, i) => (c, i)).Where(p => selected.Contains(p.c.Sha)).Select(p => p.i).ToArray();
        if (combine && indices[^1] - indices[0] + 1 != indices.Length) throw new IOException("Squash／Fixup 需要选择历史中连续的提交。");
        var steps = plan.Commits.Select((c, i) => new RewriteStep(c.Sha,
            !selected.Contains(c.Sha) || combine && i == indices[0] ? RewriteAction.Pick : action)).ToArray();
        Validate(plan, steps); return (plan, steps);
    }

    public static async Task<HistoryRewritePlan> PrepareAsync(ActionContext context, string oldest, CancellationToken cancellation = default)
    {
        var current = await RepositoryActions.ReadAsync(context.Root, cancellation);
        CheckContext(context, current, clean: true);
        var plan = await Task.Run(() =>
        {
            using var repo = new Repository(context.Root);
            oldest = RepositoryActions.CommitSha(context.Root, oldest);
            var commits = new List<RewriteCommit>(); var cursor = repo.Head.Tip;
            while (cursor is not null && commits.Count < MaximumCommits)
            {
                cancellation.ThrowIfCancellationRequested();
                var parents = cursor.Parents.ToArray();
                if (parents.Length > 1) throw new IOException("整理范围包含合并提交；请缩小到当前分支的一段线性历史。");
                commits.Add(new(cursor.Sha, cursor.Message));
                if (cursor.Sha == oldest) { commits.Reverse(); return new HistoryRewritePlan(context, parents.FirstOrDefault()?.Sha, commits.ToArray()); }
                cursor = parents.FirstOrDefault();
            }
            throw new IOException("请选择当前分支最近 100 个提交内的线性祖先。");
        }, cancellation);
        await CheckUnpublishedAsync(context.Root, oldest, cancellation);
        return plan;
    }

    public static void Validate(HistoryRewritePlan plan, IReadOnlyList<RewriteStep> steps)
    {
        if (steps.Count is < 1 or > MaximumCommits || steps.Count != plan.Commits.Count ||
            steps.Select(s => s.Sha).Distinct(StringComparer.Ordinal).Count() != steps.Count ||
            !steps.Select(s => s.Sha).ToHashSet(StringComparer.Ordinal).SetEquals(plan.Commits.Select(c => c.Sha)))
            throw new IOException("计划必须恰好包含原范围中的每个提交一次。");
        var hasPrevious = false; var messageBytes = 0;
        foreach (var step in steps)
        {
            if (!Enum.IsDefined(step.Action)) throw new IOException("不支持的历史整理动作。");
            if (step.Action == RewriteAction.Drop) continue;
            if (step.Action is RewriteAction.Squash or RewriteAction.Fixup && !hasPrevious)
                throw new IOException("Squash／Fixup 前需要一个保留的提交；不能合入范围外的提交。");
            if (step.Action == RewriteAction.Reword) { CheckMessage(step.Message); messageBytes += Encoding.UTF8.GetByteCount(step.Message); }
            hasPrevious = true;
        }
        if (!hasPrevious && plan.Base is null) throw new IOException("不能删除整个仓库的全部历史，请至少保留一个提交。");
        if (messageBytes > 64 * 1024) throw new IOException("本次修改的提交说明总计不能超过 64 KiB。");
        if (steps.All(s => s.Action == RewriteAction.Pick) && steps.Select(s => s.Sha).SequenceEqual(plan.Commits.Select(c => c.Sha)))
            throw new IOException("计划尚未修改。");
    }

    public static Task<string> ExecuteAsync(HistoryRewritePlan plan, IReadOnlyList<RewriteStep> steps)
    {
        var captured = steps.ToArray(); Validate(plan, captured);
        return RepositoryWriter.Locked(plan.Context.Root, async () =>
        {
            var current = await PrepareAsync(plan.Context, plan.Commits[0].Sha);
            if (current.Base != plan.Base || !current.Commits.Select(c => c.Sha).SequenceEqual(plan.Commits.Select(c => c.Sha)))
                throw new IOException("提交范围已变化，请重新整理。");
            await CheckWorktreeAsync(plan.Context.Root);
            var recovery = await HistoryWriter.SaveRecoveryAsync(plan.Context, null);
            var rewords = new List<RewordFile>();
            foreach (var step in captured.Where(s => s.Action == RewriteAction.Reword))
            {
                var bytes = Encoding.UTF8.GetBytes(step.Message.Replace("\r\n", "\n").TrimEnd('\n') + "\n");
                await File.WriteAllBytesAsync(Path.Combine(recovery, "message-" + step.Sha + ".txt"), bytes);
                rewords.Add(new(step.Sha, Convert.ToHexString(SHA256.HashData(bytes))));
            }
            var commentCharacter = ChooseCommentCharacter(plan, captured);
            var state = new RewordState(plan.Context.Head!, Path.GetFileName(recovery), rewords.ToArray(), commentCharacter);
            using var repo = new Repository(plan.Context.Root);
            var todo = captured.Select(s => s.Action.ToString().ToLowerInvariant() + " " + s.Sha).ToArray();
            var environment = new Dictionary<string, string> { ["GIT_SEQUENCE_EDITOR"] =
                "grove_sequence() { printf '%s\\n' " + string.Join(' ', todo.Select(Quote)) + " > \"$1\"; }; grove_sequence",
                ["GIT_EDITOR"] = MessageEditor(repo.Info.Path, state) };
            RepositoryActions.Check(plan.Context, clean: true);
            var arguments = new List<string> { "-c", "rebase.updateRefs=false", "-c", "rebase.abbreviateCommands=false", "-c", "commit.gpgSign=false", "-c", "submodule.recurse=false", "-c", "commit.cleanup=strip",
                "-c", "core.commentChar=" + commentCharacter, "-c", "core.commentString=" + commentCharacter, "rebase", "--interactive",
                "--force-rebase", "--no-autostash", "--no-autosquash", "--no-update-refs", "--no-rebase-merges", "--keep-empty", "--empty=stop", "--reapply-cherry-picks" };
            if (plan.Base is null) arguments.Add("--root"); else arguments.AddRange(["--onto", plan.Base, plan.Base]);
            try
            {
                var output = await GitWriteProcess.RepositoryAsync(plan.Context.Root, arguments, environment: environment);
                return output + "\n原历史恢复记录：" + recovery;
            }
            catch (IOException error)
            {
                using var updated = new Repository(plan.Context.Root);
                if (RepositoryOperation.Read(updated)?.Kind != "rebase") throw;
                return "历史整理已暂停，请检查 Git 输出后继续、跳过或中止。\n" + error.Message + "\n原历史恢复记录：" + recovery;
            }
            finally
            {
                // A stopped native rebase owns its todo and continues after app restart.
                var directory = Path.Combine(repo.Info.Path, "rebase-merge");
                if (Directory.Exists(directory) && File.Exists(Path.Combine(directory, "orig-head")) &&
                    File.ReadAllText(Path.Combine(directory, "orig-head")).Trim() == state.OriginalHead)
                    await File.WriteAllTextAsync(Path.Combine(directory, Marker), JsonSerializer.Serialize(state));
            }
        });
    }

    public static Task<string> RewordHeadAsync(ActionContext context, string sha, string message) => RepositoryWriter.Locked(context.Root, async () =>
    {
        CheckMessage(message); var current = await RepositoryActions.ReadAsync(context.Root); CheckContext(context, current, clean: false);
        if (sha != context.Head) throw new IOException("所选提交已不是 HEAD，请重新打开菜单。");
        await CheckUnpublishedAsync(context.Root, sha); await CheckWorktreeAsync(context.Root);
        var recovery = await HistoryWriter.SaveRecoveryAsync(context, null); RepositoryActions.Check(context);
        using var repo = new Repository(context.Root); var original = repo.Head.Tip;
        var arguments = new List<string> { "-c", "commit.gpgSign=false", "commit-tree", original.Tree.Sha };
        foreach (var parent in original.Parents) arguments.AddRange(["-p", parent.Sha]);
        arguments.Add("-F"); arguments.Add("-");
        // Creating the replacement object avoids commit --amend --only updating
        // the real index's cache-tree extension. Publish only by an old-SHA CAS.
        var replacement = (await GitWriteProcess.RunAsync(context.Root, arguments, Encoding.UTF8.GetBytes(message), noEditor: true, literalPaths: false,
            environment: new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = original.Author.Name, ["GIT_AUTHOR_EMAIL"] = original.Author.Email,
                ["GIT_AUTHOR_DATE"] = original.Author.When.ToString("O") })).Trim();
        replacement = RepositoryActions.CommitSha(context.Root, replacement); RepositoryActions.Check(context);
        var output = await GitWriteProcess.RepositoryAsync(context.Root, ["update-ref", "-m", "Grove: edit commit message", repo.Head.CanonicalName, replacement, sha]);
        return output + "\n原提交恢复记录：" + recovery;
    });

    public static Task<string> UndoAsync(ActionContext context, string sha) => RepositoryWriter.Locked(context.Root, async () =>
    {
        var current = await RepositoryActions.ReadAsync(context.Root); CheckContext(context, current, clean: false);
        if (sha != context.Head) throw new IOException("只能撤回当前 HEAD 提交。");
        using var repo = new Repository(context.Root); var parents = repo.Head.Tip.Parents.ToArray();
        if (parents.Length != 1) throw new IOException("Undo Commit 仅支持有一个父提交的 HEAD；根提交／合并提交请使用明确的 Reset 操作。");
        await CheckUnpublishedAsync(context.Root, sha); await CheckWorktreeAsync(context.Root);
        var recovery = await HistoryWriter.SaveRecoveryAsync(context, null); RepositoryActions.Check(context);
        var output = await GitWriteProcess.RepositoryAsync(context.Root, ["reset", "--soft", parents[0].Sha, "--"]);
        return output + "\n提交已撤回，索引与工作文件保留；原历史恢复记录：" + recovery;
    });

    internal static (string CommentCharacter, IReadOnlyDictionary<string, string> Environment)? RebaseSettings(Repository repo)
    {
        var directory = Path.Combine(repo.Info.Path, "rebase-merge"); var marker = Path.Combine(directory, Marker);
        if (!File.Exists(marker)) return null;
        if (new FileInfo(marker).Length > 32 * 1024) throw new IOException("历史整理恢复信息过大，请检查后在外部 Git 中处理。");
        var state = JsonSerializer.Deserialize<RewordState>(File.ReadAllText(marker)) ?? throw new IOException("历史整理恢复信息损坏。");
        if (File.ReadAllText(Path.Combine(directory, "orig-head")).Trim() != state.OriginalHead) throw new IOException("历史整理操作已变化。");
        if (state.CommentCharacter is not { Length: 1 } || !"#;@!$%^&|~:=+?`./,<>".Contains(state.CommentCharacter)) throw new IOException("历史整理说明格式无效。");
        return (state.CommentCharacter, new Dictionary<string, string> { ["GIT_EDITOR"] = MessageEditor(repo.Info.Path, state) });
    }

    private static string MessageEditor(string gitDirectory, RewordState state)
    {
        if (!Guid.TryParseExact(state.RecoveryId, "N", out _) || state.Files is null || state.Files.Length > MaximumCommits)
            throw new IOException("历史整理恢复信息无效。");
        var directory = Path.Combine(gitDirectory, "gitx-recovery", state.RecoveryId);
        string ShellPath(string path) { if (path.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new IOException("此仓库路径暂不支持历史整理。"); return Quote(OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path); }
        var cases = new StringBuilder();
        foreach (var entry in state.Files)
        {
            if (entry.Sha.Length != 40 || !entry.Sha.All(Uri.IsHexDigit)) throw new IOException("历史整理提交信息无效。");
            var file = Path.Combine(directory, "message-" + entry.Sha + ".txt");
            for (FileSystemInfo? cursor = new FileInfo(file); cursor is not null; cursor = cursor.FullName == Path.GetFullPath(gitDirectory).TrimEnd(Path.DirectorySeparatorChar) ? null : cursor is FileInfo info ? info.Directory : ((DirectoryInfo)cursor).Parent)
                if (cursor.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("历史整理恢复文件不能是链接。");
            if (new FileInfo(file).Length > 64 * 1024 + 1 || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != entry.Digest)
                throw new IOException("提交说明恢复文件已变化，未继续变基。");
            cases.Append(Quote("reword " + entry.Sha)).Append("|" + Quote("reword " + entry.Sha + " ")).Append("*) ")
                .Append("while IFS= read -r grove_line || [ -n \"$grove_line\" ]; do printf '%s\\n' \"$grove_line\"; done < ").Append(ShellPath(file)).Append(" > \"$1\";; ");
        }
        if (cases.Length > 24 * 1024) throw new IOException("提交说明恢复命令过长，请缩小本次整理范围。");
        if (state.Files.Length == 0) return "true";
        return "grove_editor() { grove_last=; while IFS= read -r grove_line || [ -n \"$grove_line\" ]; do grove_last=\"$grove_line\"; done < " +
            ShellPath(Path.Combine(gitDirectory, "rebase-merge", "done")) + "; case \"$grove_last\" in " + cases + "esac; }; grove_editor";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private static string ChooseCommentCharacter(HistoryRewritePlan plan, IReadOnlyList<RewriteStep> steps)
    {
        var starts = plan.Commits.Select(c => c.Message).Concat(steps.Where(s => s.Action == RewriteAction.Reword).Select(s => s.Message))
            .SelectMany(m => m.Split('\n')).Select(l => l.TrimStart()).Where(l => l.Length > 0).Select(l => l[0]).ToHashSet();
        foreach (var candidate in "#;@!$%^&|~:=+?`./,<>") if (!starts.Contains(candidate)) return candidate.ToString();
        throw new IOException("提交说明使用了所有可用注释前缀，请缩小整理范围，或单独修改 HEAD 的说明。");
    }
    private static void CheckMessage(string message)
    { if (string.IsNullOrWhiteSpace(message) || message.Contains('\0') || Encoding.UTF8.GetByteCount(message) > 64 * 1024) throw new IOException("提交说明必须非空、不含 NUL，且不超过 64 KiB。"); }
    private static void CheckContext(ActionContext expected, ActionContext current, bool clean)
    {
        if (expected.Root != current.Root || expected.Token != current.Token) throw new IOException("仓库已变化，请重新打开操作菜单。");
        if (current.Head is null || current.Detached) throw new IOException("请先检出有提交的本地分支。");
        if (current.InOperation) throw new IOException("请先完成或中止当前 Git 操作。");
        if (clean && current.Dirty) throw new IOException("重放历史前请先提交改动或使用 Stash 保存。");
    }
    private static async Task CheckUnpublishedAsync(string root, string sha, CancellationToken cancellation = default)
    {
        RepositoryActions.CommitSha(root, sha);
        var remote = await GitReadProcess.RunAsync(root, ["for-each-ref", "--format=%(refname)", "--contains=" + sha, "refs/remotes/"], cancellation);
        if (!string.IsNullOrWhiteSpace(remote)) throw new IOException("此提交已被本地远端跟踪引用包含，请使用 Revert；历史整理仅支持尚未发布的提交。");
    }
    private static async Task CheckWorktreeAsync(string root)
    {
        var snapshot = await WorktreeCatalog.ReadAsync(root); var current = snapshot.Entries.SingleOrDefault(w => w.Current)
            ?? throw new IOException("工作树登记已变化，请重新打开仓库。");
        if (current.Branch is { } branch && snapshot.Entries.Any(w => !w.Current && w.Branch == branch))
            throw new IOException("当前分支也在其他工作树检出，请先处理分支占用。");
    }
}
