using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;

namespace GitX.Core;

public enum FileWriteOperation { Stage, Unstage, Discard }
public sealed record RecoveredFile(string Path, bool Existed, string Content);
public sealed record DiscardRecovery(string Root, string Path, string Directory, bool Existed, string ExpectedState,
    IReadOnlyList<RecoveredFile>? Files = null);

public sealed class RepositoryWriter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public Task<DiscardRecovery?> FileAsync(DocumentRequest request, string expected, FileWriteOperation operation) => Locked(request.Root, async () =>
    {
        var paths = Paths(request);
        Check(request.Root, paths, expected);
        using var repo = new Repository(request.Root);
        if (request.Kind is not (ComparisonKind.Worktree or ComparisonKind.Index or ComparisonKind.LocalChanges)) throw new IOException("当前条目不支持此操作。");
        if (operation == FileWriteOperation.Unstage)
        {
            if (request.Kind is not (ComparisonKind.Index or ComparisonKind.LocalChanges)) throw new IOException("请选择暂存条目。");
            var arguments = repo.Head.Tip is null ? new[] { "rm", "--cached", "--force", "--" } : new[] { "restore", "--staged", "--source=HEAD", "--" };
            await GitWriteProcess.RunAsync(request.Root, arguments.Concat(paths));
        }
        else if (operation == FileWriteOperation.Stage)
        {
            if (request.Kind is not (ComparisonKind.Worktree or ComparisonKind.LocalChanges)) throw new IOException("请选择工作区条目。");
            await GitWriteProcess.RunAsync(request.Root, new[] { "add", "--" }.Concat(paths));
        }
        else
        {
            if (request.Kind is not (ComparisonKind.Worktree or ComparisonKind.LocalChanges)) throw new IOException("请选择本地改动。");
            var full = RepositoryState.CheckedPath(request.Root, request.Path);
            var directory = Path.Combine(repo.Info.Path, "gitx-recovery", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var existed = File.Exists(full);
            var affected = paths.Select((path, i) => new RecoveredFile(path,
                File.Exists(RepositoryState.CheckedPath(request.Root, path)), i == 0 ? "content" : $"content-{i}")).ToArray();
            foreach (var file in affected.Where(file => file.Existed))
                File.Copy(RepositoryState.CheckedPath(request.Root, file.Path), Path.Combine(directory, file.Content));
            var recovery = new DiscardRecovery(request.Root, request.Path, directory, existed, expected, affected);
            await File.WriteAllTextAsync(Path.Combine(directory, "recovery.json"), JsonSerializer.Serialize(recovery));
            // A second check also protects the confirmation/backup interval.
            Check(request.Root, paths, expected);
            if (request.Kind == ComparisonKind.LocalChanges)
            {
                // Restore both ends of a rename against HEAD, without changing
                // the index. Keep a backup for every affected worktree path.
                var restored = paths.Where(path => repo.Head.Tip?.Tree[path] is not null).ToArray();
                foreach (var path in restored)
                    await GitWriteProcess.RunAsync(request.Root, ["restore", "--source=HEAD", "--worktree", "--", path]);
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                foreach (var path in paths.Where(path => !restored.Contains(path, comparer)))
                {
                    var target = RepositoryState.CheckedPath(request.Root, path);
                    if (File.Exists(target)) File.Delete(target);
                }
            }
            else if (request.Kind != ComparisonKind.LocalChanges && repo.Index[request.Path] is not null) await GitWriteProcess.RunAsync(request.Root, ["restore", "--worktree", "--", request.Path]);
            else if (existed) File.Delete(full); // Exact user-confirmed file, recoverable below.
            recovery = recovery with { ExpectedState = RepositoryState.Token(request.Root, paths) };
            await File.WriteAllTextAsync(Path.Combine(directory, "recovery.json"), JsonSerializer.Serialize(recovery));
            return recovery;
        }
        return null;
    });

    public Task RestoreAsync(DiscardRecovery recovery) => Locked(recovery.Root, () =>
    {
        var files = recovery.Files ?? [new(recovery.Path, recovery.Existed, "content")];
        Check(recovery.Root, files.Select(file => file.Path), recovery.ExpectedState);
        foreach (var file in files)
        {
            var full = RepositoryState.CheckedPath(recovery.Root, file.Path);
            if (file.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.Copy(Path.Combine(recovery.Directory, file.Content), full, true);
            }
            else if (File.Exists(full)) File.Delete(full);
        }
        return Task.FromResult(true);
    });

    public Task StageSelectionAsync(DocumentRequest request, RepositoryDocument document, StageSelection selection) => Locked(request.Root, async () =>
    {
        if (request.Kind != ComparisonKind.Worktree || document.Diff is null || document.WriteToken is null) throw new IOException("只能暂存工作区文本 diff 的块或行。");
        Check(request.Root, Paths(request), document.WriteToken);
        using var repo = new Repository(request.Root);
        var index = repo.Index[request.Path];
        if (index is not null && index.Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) throw new IOException("此文件类型仅支持整文件操作。");
        var attributes = await GitReadProcess.RunAsync(request.Root, ["check-attr", "-z", "filter", "working-tree-encoding", "--", request.Path], default);
        var fields = attributes.Split('\0');
        for (var i = 2; i < fields.Length; i += 3) if (fields[i] is not ("unspecified" or "unset")) throw new IOException("自定义过滤器或工作区编码暂不支持部分暂存，请使用整文件操作。");
        var result = PartialStage.Apply(document.Diff, selection);
        if (!result.Exists) await GitWriteProcess.RunAsync(request.Root, ["update-index", "--force-remove", "--", request.Path]);
        else
        {
            var text = result.Text;
            // Preview omits a UTF-8 BOM; partial staging must preserve the index's BOM.
            if (index is not null)
            {
                using var original = repo.Lookup<Blob>(index.Id).GetContentStream();
                if (original.ReadByte() == 0xEF && original.ReadByte() == 0xBB && original.ReadByte() == 0xBF) text = "\uFEFF" + text;
            }
            else
            {
                using var original = File.OpenRead(RepositoryState.CheckedPath(request.Root, request.Path));
                if (original.ReadByte() == 0xEF && original.ReadByte() == 0xBB && original.ReadByte() == 0xBF) text = "\uFEFF" + text;
            }
            var hash = (await GitWriteProcess.RunAsync(request.Root, ["hash-object", "-w", $"--path={request.Path}", "--stdin"], Encoding.UTF8.GetBytes(text))).Trim();
            Check(request.Root, Paths(request), document.WriteToken);
            var mode = index?.Mode == Mode.ExecutableFile ? "100755" : "100644";
            await GitWriteProcess.RunAsync(request.Root, ["update-index", "--add", "--cacheinfo", mode, hash, request.Path]);
        }
        return true;
    });

    public Task<DiscardRecovery> RevertBlockAsync(DocumentRequest request, RepositoryDocument document, int block) => Locked(request.Root, async () =>
    {
        if (request.Kind is not (ComparisonKind.Worktree or ComparisonKind.LocalChanges) || document.Diff is not { Old.Exists: true, New.Exists: true } diff || document.WriteToken is null)
            throw new IOException("块回退只支持索引与工作区均存在的普通文本文件；新增或删除文件请使用整文件丢弃。");
        Check(request.Root, Paths(request), document.WriteToken);
        using var repo = new Repository(request.Root);
        if (repo.Index[request.Path]?.Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) throw new IOException("此文件类型仅支持整文件操作。");
        var attributes = await GitReadProcess.RunAsync(request.Root, ["check-attr", "-z", "filter", "working-tree-encoding", "--", request.Path], default);
        var fields = attributes.Split('\0');
        for (var i = 2; i < fields.Length; i += 3) if (fields[i] is not ("unspecified" or "unset")) throw new IOException("自定义过滤器或工作区编码暂不支持块回退，请使用整文件操作。");
        var text = PartialStage.RevertBlock(diff, block);
        var full = RepositoryState.CheckedPath(request.Root, request.Path);
        var bytes = await File.ReadAllBytesAsync(full);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) text = "\uFEFF" + text;
        var directory = Path.Combine(repo.Info.Path, "gitx-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "content"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, "path.txt"), request.Path);
        var temporary = full + ".gitx-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false));
            Check(request.Root, Paths(request), document.WriteToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(full));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var recovery = new DiscardRecovery(request.Root, request.Path, directory, true, RepositoryState.Token(request.Root, [request.Path]));
        await File.WriteAllTextAsync(Path.Combine(directory, "recovery.json"), JsonSerializer.Serialize(recovery));
        return recovery;
    });

    public Task<string> CommitAsync(string root, string expected, string message) => Locked(root, async () =>
    {
        if (string.IsNullOrWhiteSpace(message)) throw new IOException("请填写提交说明。");
        Check(root, [], expected);
        var changed = await GitReadProcess.RunAsync(root, ["diff", "--cached", "--name-only", "-z"], default);
        if (changed.Length == 0) throw new IOException("没有暂存改动，未创建空提交。");
        return await GitWriteProcess.RunAsync(root, ["commit", "--file=-"], Encoding.UTF8.GetBytes(message), literalPaths: false);
    });

    private static string[] Paths(DocumentRequest request) => request.OldPath is { } old && old != request.Path ? [request.Path, old] : [request.Path];
    private static void Check(string root, IEnumerable<string> paths, string expected)
    {
        using var repo = new Repository(root);
        if (repo.Index.Conflicts.Any()) throw new IOException("仓库存在未解决冲突，请先处理冲突。");
        if (RepositoryState.Token(root, paths) != expected) throw new IOException("文件、索引或 HEAD 已变化，本次操作未执行。请刷新并重新检查改动。");
    }

    internal static async Task<T> Locked<T>(string root, Func<Task<T>> action)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), _ => new(1, 1));
        await gate.WaitAsync();
        try { return await Task.Run(action); }
        finally { gate.Release(); }
    }
}
