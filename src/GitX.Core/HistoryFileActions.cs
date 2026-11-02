using System.Text;
using System.Text.Json;
using LibGit2Sharp;

namespace GitX.Core;

public enum HistoryFileOperation { CherryPick, Revert, GetRevision }
public sealed record HistoryFileChange(string Path, string OldPath, string Status, bool BeforeExists, bool AfterExists);
public sealed record HistoryFileSelection(string Root, string Commit, string? Parent, IReadOnlyList<string> Parents, IReadOnlyList<HistoryFileChange> Files);
public sealed record HistoryFileWritePlan(HistoryFileSelection Selection, HistoryFileOperation Operation, ActionContext Context, string State);

// Changes selected in the Log are immutable commit deltas. Applying/reversing
// them only changes the worktree; it never cherry-picks an entire commit.
public static class HistoryFileActions
{
    public static Task<HistoryFileSelection> ReadAsync(string root, string commit, IEnumerable<string> selected, CancellationToken token = default)
    {
        var paths = selected.Distinct(StringComparer.Ordinal).ToArray();
        return Task.Run(() =>
        {
            if (paths.Length is 0 or > 1000) throw new IOException("请选择 1–1,000 个提交文件。");
            using var repo = new Repository(root);
            var revision = repo.Lookup<Commit>(commit) ?? throw new IOException("提交已不存在，请刷新。");
            var parents = revision.Parents.Select(p => p.Sha).ToArray(); var parent = revision.Parents.FirstOrDefault();
            using var changes = repo.Diff.Compare<TreeChanges>(parent?.Tree, revision.Tree, new CompareOptions { Similarity = SimilarityOptions.None });
            var files = changes.Where(c => paths.Contains(c.Path, StringComparer.Ordinal)).Select(c =>
            {
                token.ThrowIfCancellationRequested();
                var oldPath = string.IsNullOrEmpty(c.OldPath) ? c.Path : c.OldPath;
                return new HistoryFileChange(c.Path, oldPath, c.Status.ToString(), parent?.Tree[oldPath] is not null, revision.Tree[c.Path] is not null);
            }).ToArray();
            if (files.Length != paths.Length) throw new IOException("所选文件不属于此提交的第一父差异，请重新选择。");
            return new HistoryFileSelection(Path.GetFullPath(root), revision.Sha, parents.FirstOrDefault(), parents, files);
        }, token);
    }

    public static async Task<byte[]> PatchAsync(HistoryFileSelection selection, CancellationToken token = default)
    {
        var args = selection.Parent is { } parent ? new List<string> { "diff", parent, selection.Commit } : ["show", "--format=", "--root", selection.Commit];
        args.AddRange(["--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--"]);
        // A patch can contain non-UTF-8 text bytes. The one-byte mapping preserves
        // them through the bounded process reader without replacement characters.
        var patch = await GitReadProcess.RunAsync(selection.Root, args.Concat(Paths(selection)), token, outputEncoding: Encoding.Latin1);
        return Encoding.Latin1.GetBytes(patch);
    }
    private static string[] Paths(HistoryFileSelection selection) => selection.Files.SelectMany(f => new[] { f.Path, f.OldPath }).Distinct(StringComparer.Ordinal).ToArray();

    public static async Task<HistoryFileWritePlan> PrepareAsync(HistoryFileSelection selection, HistoryFileOperation operation, CancellationToken token = default)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        selection = await ReadAsync(selection.Root, selection.Commit, selection.Files.Select(f => f.Path), token);
        var context = await RepositoryActions.ReadAsync(selection.Root, token);
        if (context.InOperation) throw new IOException("请先完成或中止当前 Git 操作，处理未合并索引。");
        var paths = Paths(selection);
        await Task.Run(() => ValidateFiles(selection), token);
        await ValidateAttributesAsync(selection.Root, paths, token);
        var state = await Task.Run(() => RepositoryState.Token(selection.Root, paths, token), token);
        return new(selection, operation, context, state);
    }

    public static Task<DiscardRecovery> ExecuteAsync(HistoryFileWritePlan plan) => RepositoryWriter.Locked(plan.Selection.Root, async () =>
    {
        var selection = plan.Selection; var paths = Paths(selection);
        Check(plan); ValidateFiles(selection); await ValidateAttributesAsync(selection.Root, paths, default);
        byte[]? patch = null;
        var apply = new List<string> { "apply", "--binary", "--whitespace=nowarn" };
        if (plan.Operation == HistoryFileOperation.Revert) apply.Add("--reverse");
        if (plan.Operation != HistoryFileOperation.GetRevision)
        {
            patch = await PatchAsync(selection);
            await GitWriteProcess.RunAsync(selection.Root, apply.Concat(["--check", "-"]), patch);
        }
        Check(plan);
        using var repo = new Repository(selection.Root);
        var directory = Path.Combine(repo.Info.Path, "gitx-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = paths.Select((path, index) => new RecoveredFile(path, File.Exists(RepositoryState.CheckedPath(selection.Root, path)), $"content-{index}")).ToArray();
        foreach (var file in files.Where(f => f.Existed)) File.Copy(RepositoryState.CheckedPath(selection.Root, file.Path), Path.Combine(directory, file.Content));
        var recovery = new DiscardRecovery(selection.Root, $"历史文件操作 · {selection.Files.Count} 个文件", directory, true, plan.State, files);
        var record = Path.Combine(directory, "recovery.json");
        await File.WriteAllTextAsync(record, JsonSerializer.Serialize(recovery));
        Check(plan);
        try
        {
            if (plan.Operation == HistoryFileOperation.GetRevision)
            {
                var present = selection.Files.Where(f => f.AfterExists).Select(f => f.Path).ToArray();
                if (present.Length > 0) await GitWriteProcess.RunAsync(selection.Root, new[] { "restore", "--worktree", "--source=" + selection.Commit, "--" }.Concat(present));
                foreach (var file in selection.Files.Where(f => !f.AfterExists))
                { var full = RepositoryState.CheckedPath(selection.Root, file.Path); if (File.Exists(full)) File.Delete(full); }
            }
            else await GitWriteProcess.RunAsync(selection.Root, apply.Concat(["-"]), patch);
            recovery = recovery with { ExpectedState = RepositoryState.Token(selection.Root, paths) };
            await File.WriteAllTextAsync(record, JsonSerializer.Serialize(recovery));
            return recovery;
        }
        catch (Exception ex) { throw new IOException($"历史文件操作未能完成：{ex.Message}\n原文件备份：{directory}。请核对工作区，不要直接重试。", ex); }
    });

    private static void Check(HistoryFileWritePlan plan)
    {
        if (!Enum.IsDefined(plan.Operation) || !Path.GetFullPath(plan.Context.Root).Equals(Path.GetFullPath(plan.Selection.Root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("历史文件操作上下文无效。");
        RepositoryActions.Check(plan.Context);
        if (RepositoryState.Token(plan.Selection.Root, Paths(plan.Selection)) != plan.State) throw new IOException("所选文件、索引或 HEAD 已变化，未执行操作。请重新打开菜单。");
    }
    private static void ValidateFiles(HistoryFileSelection selection)
    {
        using var repo = new Repository(selection.Root);
        var commit = repo.Lookup<Commit>(selection.Commit) ?? throw new IOException("提交已不存在。");
        var parent = commit.Parents.FirstOrDefault();
        foreach (var file in selection.Files)
        {
            RepositoryState.CheckedPath(selection.Root, file.Path); RepositoryState.CheckedPath(selection.Root, file.OldPath);
            foreach (var entry in new[] { parent?.Tree[file.OldPath], commit.Tree[file.Path] }.Where(e => e is not null))
                if (entry!.Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) throw new IOException("所选变化包含链接、子模块或其他特殊类型，暂不支持写入。");
        }
        var paths = Paths(selection);
        if (OperatingSystem.IsWindows() && paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new IOException("所选路径存在大小写冲突，暂不支持在 Windows 写入。");
    }
    private static async Task ValidateAttributesAsync(string root, string[] paths, CancellationToken token)
    {
        var output = await GitReadProcess.RunAsync(root, new[] { "check-attr", "-z", "filter", "working-tree-encoding", "--" }.Concat(paths), token);
        var fields = output.Split('\0');
        for (var i = 0; i + 2 < fields.Length; i += 3)
            if (fields[i + 2] is not ("unspecified" or "unset")) throw new IOException("所选文件使用自定义 filter 或 working-tree-encoding，暂不支持历史文件写入。");
    }
}
