using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

// A null Lines value includes the whole file. Otherwise anchors refer to the
// captured HEAD -> worktree diff, never to a filtered/renumbered display row.
public sealed record CommitFileSelection(DocumentRequest Request, RepositoryDocument Document, IReadOnlySet<ChangeLine>? Lines);
public sealed record SelectionCommitResult(string Sha, string Output, string? Warning, string? Notice = null);

public sealed class CommitSelectionWriter
{
    private static readonly string[] TemporaryIndexNames = ["original.index", "commit.index", "remaining.index", "reconciled.index"];

    public Task<SelectionCommitResult> CommitAsync(string root, IReadOnlyList<CommitFileSelection> files, string message)
        => RepositoryWriter.Locked<SelectionCommitResult>(root, async () =>
    {
        if (string.IsNullOrWhiteSpace(message)) throw new IOException("请填写提交说明。");
        if (files.Count == 0 || files.Select(file => file.Request.Path).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new IOException("请选择要提交的文件或变更行。");
        var paths = files.SelectMany(file => file.Request.OldPath is { } old && old != file.Request.Path
            ? new[] { old, file.Request.Path } : [file.Request.Path]).Distinct(StringComparer.Ordinal).ToArray();
        var expected = RepositoryState.Token(root, paths);
        using var repo = new Repository(root);
        if (repo.Index.Conflicts.Any()) throw new IOException("请先解决索引冲突。");
        if (RepositoryOperation.Read(repo) is { } operation)
            throw new IOException($"仓库正在{operation.Label}，请使用“继续操作”提交完整操作结果，不能选择部分改动提交。");
        var metadata = repo.Info.Path;
        var indexPath = Path.Combine(metadata, "index");
        if (File.Exists(indexPath + ".lock")) throw new IOException("Git 索引正被其他操作占用，未创建提交。");
        var objectIdBytes = repo.Config.Get<string>("extensions.objectFormat")?.Value == "sha256" ? 32 : 20;
        var originalIndex = IndexFingerprint.Read(indexPath, objectIdBytes, default);
        var directory = Path.Combine(metadata, "gitx-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (File.Exists(indexPath)) File.Copy(indexPath, Path.Combine(directory, "original.index"));
        var commitIndex = Path.Combine(directory, "commit.index");
        var remainingIndex = Path.Combine(directory, "remaining.index");
        var commitEnvironment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = commitIndex };
        var remainingEnvironment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = remainingIndex };
        await GitWriteProcess.RunAsync(root, repo.Head.Tip is null ? ["read-tree", "--empty"] : ["read-tree", "HEAD"], environment: commitEnvironment);
        if (File.Exists(indexPath)) File.Copy(indexPath, remainingIndex);
        else await GitWriteProcess.RunAsync(root, ["read-tree", "--empty"], environment: remainingEnvironment);
        using var reader = new RepositoryReader();
        foreach (var file in files)
        {
            if (file.Request.Root != root || file.Request.Kind != ComparisonKind.LocalChanges || file.Document.Path != file.Request.Path
                || file.Document.WriteToken is not { } token || token != RepositoryState.Token(root, FilePaths(file.Request)))
                throw new IOException("提交范围所属的文件、索引或 HEAD 已变化，请重新检查。");
            if (file.Lines is null)
            {
                await GitWriteProcess.RunAsync(root, new[] { "add", "-A", "--" }.Concat(FilePaths(file.Request)), environment: commitEnvironment);
                // A whole-file inclusion consumes its existing staged changes too.
                await CopyEntryAsync(root, file.Request.Path, commitEnvironment, remainingEnvironment);
                if (file.Request.OldPath is { } old && old != file.Request.Path)
                    await CopyEntryAsync(root, old, commitEnvironment, remainingEnvironment);
                continue;
            }
            if (file.Document.Diff is not { } diff || file.Request.OldPath is { } oldPath && oldPath != file.Request.Path)
                throw new IOException("该文件只支持整文件包含。");
            var attributes = await GitReadProcess.RunAsync(root, ["check-attr", "-z", "filter", "working-tree-encoding", "--", file.Request.Path], default);
            var fields = attributes.Split('\0');
            for (var i = 2; i < fields.Length; i += 3) if (fields[i] is not ("unspecified" or "unset"))
                throw new IOException("自定义过滤器或工作区编码暂只支持整文件提交。");
            var mode = repo.Index[file.Request.Path]?.Mode ?? repo.Head.Tip?.Tree[file.Request.Path]?.Mode ?? Mode.NonExecutableFile;
            if (mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) throw new IOException("此文件类型仅支持整文件包含。");
            var selected = PartialStage.ApplyRows(diff, file.Lines);
            var headBlob = repo.Head.Tip?.Tree[file.Request.Path]?.Target as Blob;
            var bom = headBlob is not null ? HasBom(headBlob) : WorktreeBom(root, file.Request.Path);
            await SetTextAsync(root, file.Request.Path, selected.Text, selected.Exists, mode, bom, commitEnvironment);

            // Reconcile only the selected worktree delta into the original
            // index. Already staged, unselected rows stay staged after commit.
            var indexed = await reader.DocumentAsync(new(root, file.Request.Path, ComparisonKind.Index, Whitespace: file.Request.Whitespace));
            var working = await reader.DocumentAsync(new(root, file.Request.Path, ComparisonKind.Worktree, Whitespace: file.Request.Whitespace));
            if (working.WriteToken != file.Document.WriteToken || indexed.Diff is null || working.Diff is null)
                throw new IOException("提交期间文件已变化，请重新检查。");
            var indexToHead = SourceMap(indexed.Diff);
            var indexRows = working.Diff.Blocks.SelectMany(block => block.Rows).Where(row => row.NewLine is { } line
                ? file.Lines.Contains(new(1, line))
                : row.OldLine is { } indexLine && indexToHead.TryGetValue(indexLine, out var headLine) && file.Lines.Contains(new(0, headLine)))
                .Select(ChangeLine.For).ToHashSet();
            if (indexRows.Count > 0)
            {
                var remaining = PartialStage.ApplyRows(working.Diff, indexRows);
                var indexBlob = repo.Index[file.Request.Path] is { } entry ? repo.Lookup<Blob>(entry.Id) : null;
                await SetTextAsync(root, file.Request.Path, remaining.Text, remaining.Exists, mode,
                    indexBlob is not null ? HasBom(indexBlob) : WorktreeBom(root, file.Request.Path), remainingEnvironment);
            }
        }
        var plannedTree = (await GitWriteProcess.RunAsync(root, ["write-tree"], environment: commitEnvironment)).Trim();
        var remainingTree = (await GitWriteProcess.RunAsync(root, ["write-tree"], environment: remainingEnvironment)).Trim();
        // Check that this Git can reconcile remaining staging before advancing
        // HEAD. With the planned tree as one side, this is a trivial tree-only merge.
        if (remainingTree != plannedTree)
            await MergeTreesAsync(root, plannedTree, plannedTree, remainingTree);
        if (RepositoryState.Token(root, paths) != expected) throw new IOException("提交期间仓库已变化，未创建提交。");
        var output = await GitWriteProcess.RunAsync(root, ["commit", "--file=-"], Encoding.UTF8.GetBytes(message), literalPaths: false, environment: commitEnvironment);
        using var completed = new Repository(root);
        var head = completed.Head.Tip ?? throw new IOException("Git 没有返回提交结果，请刷新检查。");
        string? warning = null;
        var lockPath = indexPath + ".lock";
        var ownLock = false;
        try
        {
            var installIndex = remainingIndex;
            if (head.Tree.Sha != plannedTree)
                installIndex = await ReconcileHookAsync(root, completed, plannedTree, head.Tree, remainingTree, remainingIndex, directory);
            using (var locked = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownLock = true;
                if (IndexFingerprint.Read(indexPath, objectIdBytes, default) != originalIndex) throw new IOException("索引被外部进程修改");
                var bytes = await File.ReadAllBytesAsync(installIndex);
                await locked.WriteAsync(bytes); locked.Flush(true);
            }
            if (IndexFingerprint.Read(indexPath, objectIdBytes, default) != originalIndex) throw new IOException("索引被外部进程修改");
            var currentHead = (await GitReadProcess.RunAsync(root, ["rev-parse", "HEAD"], default)).Trim();
            if (currentHead != head.Sha) throw new IOException("HEAD 被其他操作修改");
            File.Move(lockPath, indexPath, true); ownLock = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibGit2SharpException)
        { warning = $"提交已完成，但未同步索引：{e.Message}。HEAD 已前进，原索引可能包含反向暂存；请核对后处理，不要直接提交或重试本次提交。原索引及剩余暂存备份：{directory}"; }
        finally { if (ownLock) File.Delete(lockPath); }
        string? notice = null;
        if (warning is null)
        {
            try { CleanTemporaryIndexes(metadata, directory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { notice = $"索引已同步，但本次临时索引未清理：{e.Message}。保留位置：{directory}"; }
        }
        if (notice is not null)
        {
            output += "\n" + notice;
            GitWriteOperation.Current?.Append("\n" + notice + "\n");
        }
        return new(head.Sha, output, warning, notice);
    });

    private static void CleanTemporaryIndexes(string metadata, string directory)
    {
        // Only this call's successful scratch indexes are disposable. Never
        // sweep older folders or infer recovery value from age or filenames.
        var recoveryRoot = Path.GetFullPath(Path.Combine(metadata, "gitx-recovery"));
        directory = Path.GetFullPath(directory);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!comparer.Equals(Path.GetDirectoryName(directory), recoveryRoot) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            throw new IOException("临时索引路径不在本次恢复目录内");
        if (!Directory.Exists(directory) && !File.Exists(directory)) return;
        if (((File.GetAttributes(recoveryRoot) | File.GetAttributes(directory)) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("恢复目录包含链接，未进行清理");
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(TemporaryIndexNames.Length + 1).ToArray();
        if (entries.Length > TemporaryIndexNames.Length || entries.Any(path => !TemporaryIndexNames.Contains(Path.GetFileName(path), comparer)
            || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
            throw new IOException("目录包含其他文件、子目录或链接，已保留");
        foreach (var name in TemporaryIndexNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) File.Delete(path);
        }
        // Non-recursive: a concurrently added file is retained, never removed.
        Directory.Delete(directory);
    }

    private static async Task<string> MergeTreesAsync(string root, string baseline, string ours, string theirs)
        => (await GitWriteProcess.RunAsync(root, ["merge-tree", "--write-tree", "--no-messages", $"--merge-base={baseline}", ours, theirs])).Trim();

    private static async Task<string> ReconcileHookAsync(string root, Repository repo, string plannedTree, Tree actualTree, string remainingTree, string remainingIndex, string directory)
    {
        // Replay P -> R (unselected staged changes) onto the hook's actual tree A.
        // All merging is object-only; neither the worktree nor the real index is
        // touched until the normal fingerprint check and owned index.lock swap.
        var mergedTree = actualTree.Sha;
        if (remainingTree != plannedTree)
            mergedTree = await MergeTreesAsync(root, plannedTree, actualTree.Sha, remainingTree);
        var before = repo.Lookup<Tree>(remainingTree) ?? throw new IOException("无法读取剩余暂存树。");
        var after = repo.Lookup<Tree>(mergedTree) ?? throw new IOException("无法读取 hook 后的索引合并结果。");
        var reconciledIndex = Path.Combine(directory, "reconciled.index");
        File.Copy(remainingIndex, reconciledIndex);
        var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = reconciledIndex };
        using var changes = repo.Diff.Compare<TreeChanges>(before, after, new CompareOptions { Similarity = SimilarityOptions.None });
        // Update only hook-affected entries, retaining unrelated index flags
        // (including intent-to-add/skip-worktree) and recovery extensions.
        var paths = changes.Select(change => change.Path).OrderBy(path => after[path] is null ? 0 : 1);
        foreach (var path in paths)
        {
            if (after[path] is not { } entry)
                await GitWriteProcess.RunAsync(root, ["update-index", "--force-remove", "--", path], environment: environment);
            else
                await GitWriteProcess.RunAsync(root, ["update-index", "--add", "--cacheinfo", Convert.ToString((int)entry.Mode, 8), entry.Target.Id.Sha, path], environment: environment);
        }
        var installedTree = (await GitWriteProcess.RunAsync(root, ["write-tree"], environment: environment)).Trim();
        if (installedTree != mergedTree) throw new IOException("hook 后索引校验失败。");
        return reconciledIndex;
    }

    private static string[] FilePaths(DocumentRequest request) => request.OldPath is { } old && old != request.Path ? [old, request.Path] : [request.Path];
    private static bool HasBom(Blob blob)
    { using var stream = blob.GetContentStream(); return stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF; }
    private static bool WorktreeBom(string root, string path)
    {
        var full = RepositoryState.CheckedPath(root, path); if (!File.Exists(full)) return false;
        using var stream = File.OpenRead(full); return stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF;
    }
    private static async Task SetTextAsync(string root, string path, string text, bool exists, Mode mode, bool bom, IReadOnlyDictionary<string, string> environment)
    {
        if (!exists) { await GitWriteProcess.RunAsync(root, ["update-index", "--force-remove", "--", path], environment: environment); return; }
        var bytes = Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + text);
        var sha = (await GitWriteProcess.RunAsync(root, ["hash-object", "-w", $"--path={path}", "--stdin"], bytes)).Trim();
        await GitWriteProcess.RunAsync(root, ["update-index", "--add", "--cacheinfo", Convert.ToString((int)mode, 8), sha, path], environment: environment);
    }
    private static async Task CopyEntryAsync(string root, string path, IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> destination)
    {
        var entry = await GitReadProcess.RunAsync(root, ["ls-files", "--stage", "-z", "--", path], default, environment: source);
        if (entry.Length == 0) await GitWriteProcess.RunAsync(root, ["update-index", "--force-remove", "--", path], environment: destination);
        else
        {
            var parts = entry.Split([' ', '\t'], 4);
            await GitWriteProcess.RunAsync(root, ["update-index", "--add", "--cacheinfo", parts[0], parts[1], path], environment: destination);
        }
    }
    private static Dictionary<int, int> SourceMap(SideBySideDiff diff)
    {
        var result = new Dictionary<int, int>(); var oldEnd = 0; var newEnd = 0;
        foreach (var block in diff.Blocks)
        {
            for (var i = 0; i < block.NewStart - newEnd; i++) result[newEnd + i + 1] = oldEnd + i + 1;
            foreach (var row in block.Rows) if (row.NewLine is { } added && row.OldLine is { } old) result[added] = old;
            oldEnd = block.OldStart + block.OldCount; newEnd = block.NewStart + block.NewCount;
        }
        for (var i = 0; i < NativeDiff.Lines(diff.New.Text).Length - newEnd; i++) result[newEnd + i + 1] = oldEnd + i + 1;
        return result;
    }
}
