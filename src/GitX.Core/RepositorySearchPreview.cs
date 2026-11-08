using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

// Deliberately not RepositoryDocument: a search preview cannot carry editing,
// comment, history or tab identity into the main editor.
public sealed record CodeSearchPreview(string RootKey, string Path, string Text,
    int FirstLine, int SelectedLine, int TotalLines, bool Truncated)
{
    public string Source => "工作区磁盘 · 只读预览（不含未保存编辑）";
}

public sealed partial class RepositoryReader
{
    public const int MaxSearchPreviewLines = 81;
    public const int MaxSearchPreviewCharacters = 32_000;

    public static Task<CodeSearchPreview> SearchPreviewAsync(string root, CodeSearchMatch match,
        CancellationToken token = default) => ReadWork.Cpu.Run(() => ReadSearchPreview(root, match, token), token);

    private static CodeSearchPreview ReadSearchPreview(string root, CodeSearchMatch match, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (match.Column < 1) throw new IOException("预览目标列号无效。");
        var path = match.Path;
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') ||
            path.Split('/', '\\').Any(part => part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("预览只接受当前工作树内的相对文件路径。");
        using var repo = new Repository(root);
        if (repo.Info.IsBare || WorkingTextFiles.RootKey(root) != WorkingTextFiles.RootKey(repo.Info.WorkingDirectory))
            throw new IOException("预览仓库已变化，请重新搜索。");
        var full = RepositoryState.CheckedPath(repo.Info.WorkingDirectory, path);
        var before = WorkingTextFiles.Stamp(full);
        // Shared bounded UTF-8 file policy, without Capture's editable baseline.
        var bytes = WorkingTextFiles.ReadBytes(full, token);
        var after = WorkingTextFiles.Stamp(RepositoryState.CheckedPath(repo.Info.WorkingDirectory, path));
        if (before != after || bytes.LongLength != after.Length)
            throw new IOException("读取期间文件发生变化，请重新选择结果。");
        token.ThrowIfCancellationRequested();
        var (text, problem) = DecodeText(bytes);
        if (problem is not null) throw new IOException(problem);
        if (match.Fingerprint is not null && match.Fingerprint != SearchFingerprint(text!))
            throw new IOException("搜索后文件内容已变化，请按 F5 重新搜索。");
        token.ThrowIfCancellationRequested();
        var lines = text!.Split('\n');
        if (match.Line < 1 || match.Line > lines.Length)
            throw new IOException($"目标行已超出文件范围（共 {lines.Length:N0} 行），请重新搜索。");
        var first = Math.Max(1, match.Line - MaxSearchPreviewLines / 2);
        var last = Math.Min(lines.Length, first + MaxSearchPreviewLines - 1);
        // Budget each line so even an allowed 20,000-character line cannot
        // hide the selected result behind earlier context. No unbounded UI text.
        var lineBudget = MaxSearchPreviewCharacters / MaxSearchPreviewLines - 16;
        var output = new StringBuilder(); var shortened = false;
        for (var line = first; line <= last; line++)
        {
            token.ThrowIfCancellationRequested();
            var content = lines[line - 1].TrimEnd('\r');
            var from = line == match.Line ? Math.Max(0, Math.Min(content.Length, match.Column - 1) - lineBudget / 2) : 0;
            var count = Math.Min(lineBudget, content.Length - from);
            var clipped = from > 0 || count < content.Length;
            shortened |= clipped;
            output.Append(line == match.Line ? '›' : ' ').Append(line.ToString().PadLeft(6)).Append("  ");
            if (from > 0) output.Append('…');
            output.Append(content, from, count);
            if (from + count < content.Length) output.Append('…');
            if (line < last) output.Append('\n');
        }
        token.ThrowIfCancellationRequested();
        return new(WorkingTextFiles.RootKey(root), path, output.ToString(), first, match.Line, lines.Length,
            first > 1 || last < lines.Length || shortened);
    }
}
