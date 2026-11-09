using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

public sealed class ConflictWriter
{
    public Task<string> SaveAsync(ConflictDocument document, string expected, string result) => RepositoryWriter.Locked(document.Root, async () =>
    {
        Check(document.Root, document.Path, expected);
        if (!document.IsText) throw new IOException("此冲突不支持文本编辑。");
        if (await ConflictReader.HasCustomAttributesAsync(document.Root, document.Path)) throw new IOException("文件过滤器或工作区编码已变化，请重新打开冲突。");
        if (Encoding.UTF8.GetByteCount(result) > RepositoryReader.MaxTextBytes) throw new IOException("合并结果超过 1 MiB，未保存。");
        var full = RepositoryState.CheckedPath(document.Root, document.Path);
        Backup(document.Root, document.Path);
        Check(document.Root, document.Path, expected);
        var text = result.Replace("\r\n", "\n");
        if (document.LineEnding == "CRLF") text = text.Replace("\n", "\r\n");
        var temporary = full + ".gitx-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(document.Bom));
            if (!OperatingSystem.IsWindows() && File.Exists(full)) File.SetUnixFileMode(temporary, File.GetUnixFileMode(full));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return RepositoryState.Token(document.Root, [document.Path]);
    });

    public Task<string> ChooseSideAsync(ConflictDocument document, string expected, bool left) => RepositoryWriter.Locked(document.Root, async () =>
    {
        Check(document.Root, document.Path, expected);
        var side = left ? document.Left : document.Right;
        if (side.Path != document.Path || side.Exists && side.Mode is not (33188 or 33261)) throw new IOException("该路径类型需外部处理，不能自动采用此侧。");
        var full = RepositoryState.CheckedPath(document.Root, document.Path);
        Backup(document.Root, document.Path); Check(document.Root, document.Path, expected);
        if (side.Exists) await GitWriteProcess.RunAsync(document.Root, ["checkout", left ? "--ours" : "--theirs", "--", document.Path]);
        else if (File.Exists(full)) File.Delete(full);
        return RepositoryState.Token(document.Root, [document.Path]);
    });

    public Task ResolveAsync(ConflictDocument document, string expected) => RepositoryWriter.Locked(document.Root, async () =>
    {
        Check(document.Root, document.Path, expected);
        var full = RepositoryState.CheckedPath(document.Root, document.Path);
        var markerSize = await ConflictReader.MarkerSizeAsync(document.Root, document.Path);
        if (markerSize != document.MarkerSize) throw new IOException("冲突标记配置已变化，请重新打开冲突。");
        if (document.IsText && File.Exists(full) && ConflictReader.ContainsMarkers(await File.ReadAllTextAsync(full), markerSize)) throw new IOException("文件仍含冲突标记，请处理后再标记解决。");
        await GitWriteProcess.RunAsync(document.Root, ["add", "-A", "--", document.Path]);
        using var repo = new Repository(document.Root);
        if (repo.Index.Conflicts.Any(c => c.Ancestor?.Path == document.Path || c.Ours?.Path == document.Path || c.Theirs?.Path == document.Path)) throw new IOException("索引仍包含此路径的未合并条目。");
        return true;
    });

    public Task OperationAsync(string root, string expected, RepositoryOperation operation, bool abort, bool skip = false) => RepositoryWriter.Locked(root, async () =>
    {
        using var repo = new Repository(root);
        if (RepositoryState.Token(root, []) != expected || RepositoryOperation.Read(repo)?.Kind != operation.Kind) throw new IOException("Git 操作状态已变化，请刷新后重试。");
        if (abort && skip || skip && operation.Kind is not ("rebase" or "cherry-pick" or "revert")) throw new IOException("当前操作不支持跳过提交。");
        if (!abort && !skip && repo.Index.Conflicts.Any()) throw new IOException("还有未解决的索引冲突。");
        if (operation.Kind is not ("merge" or "rebase" or "cherry-pick" or "revert")) throw new IOException("不支持此操作。");
        // Abort can discard conflict resolutions; retain exact current files first.
        if (abort || skip)
            foreach (var path in repo.RetrieveStatus(new StatusOptions { IncludeIgnored = false, IncludeUntracked = true, RecurseUntrackedDirs = true })
                .Where(s => s.State != FileStatus.Unaltered).Select(s => s.FilePath).Distinct()) Backup(root, path);
        var settings = !abort && operation.Kind == "rebase" ? HistoryRewrite.RebaseSettings(repo) : null;
        var arguments = new List<string>();
        if (settings is { } editor) arguments.AddRange(["-c", "rebase.abbreviateCommands=false", "-c", "commit.gpgSign=false", "-c", "submodule.recurse=false", "-c", "commit.cleanup=strip", "-c", "core.commentChar=" + editor.CommentCharacter, "-c", "core.commentString=" + editor.CommentCharacter]);
        arguments.AddRange([operation.Kind, abort ? "--abort" : skip ? "--skip" : "--continue"]);
        try { await GitWriteProcess.RepositoryAsync(root, arguments, environment: settings?.Environment); }
        catch (IOException) when (!abort && operation.Kind is "rebase" or "cherry-pick" or "revert")
        {
            // Git returns 1 when continuing successfully reaches the next conflict.
            // Only this observable state is normal progress; hook failures still surface.
            using var updated = new Repository(root);
            if (!updated.Index.Conflicts.Any() || RepositoryOperation.Read(updated)?.Kind != operation.Kind) throw;
        }
        return true;
    });

    private static void Check(string root, string path, string expected)
    {
        if (RepositoryState.Token(root, [path]) != expected) throw new IOException("文件、索引或 Git 操作已变化。未覆盖内容，请关闭并重新打开冲突。");
        using var repo = new Repository(root);
        if (!repo.Index.Conflicts.Any(c => c.Ancestor?.Path == path || c.Ours?.Path == path || c.Theirs?.Path == path)) throw new IOException("该路径已无冲突，未执行操作。");
    }

    private static void Backup(string root, string path)
    {
        var full = RepositoryState.CheckedPath(root, path);
        using var repo = new Repository(root);
        var directory = Path.Combine(repo.Info.Path, "gitx-recovery", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        if (File.Exists(full)) File.Copy(full, Path.Combine(directory, "content"));
        File.WriteAllText(Path.Combine(directory, "path.txt"), path);
    }
}
