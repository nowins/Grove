using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

public sealed partial class ConflictReader
{
    public async Task<ConflictDocument> ReadAsync(string root, string path, CancellationToken token = default)
    {
        var markerSize = await MarkerSizeAsync(root, path, token);
        var document = await ReadWork.Cpu.Run(() => Capture(root, path, markerSize, token), token);
        if (!document.IsText) return document;
        if (await HasCustomAttributesAsync(root, path, token))
            return document with { Limitation = "自定义过滤器或工作区编码：请选择完整一侧，或在外部工具处理后标记解决。" };
        var (result, blocks) = await MergeAsync(document.Base.Text!, document.Left.Text!, document.Right.Text!, token);
        var existing = document.ExistingResult;
        if (existing) { result = document.Result!; blocks = []; }
        var layout = await LayoutAsync(document.Left.Text!, result, document.Right.Text!, token);
        if (document.Token != await ReadWork.Cpu.Run(() => RepositoryState.Token(root, [path], token), token)) throw new IOException("冲突读取期间仓库发生变化，请重新打开。");
        return document with { Result = result, Blocks = blocks, Layout = layout };
    }

    internal static async Task<bool> HasCustomAttributesAsync(string root, string path, CancellationToken token = default)
    {
        var attributes = await GitReadProcess.RunAsync(root, ["check-attr", "-z", "filter", "working-tree-encoding", "--", path], token);
        var fields = attributes.Split('\0');
        for (var i = 2; i < fields.Length; i += 3) if (fields[i] is not ("unspecified" or "unset")) return true;
        return false;
    }

    internal static async Task<int> MarkerSizeAsync(string root, string path, CancellationToken token = default)
    {
        var attributes = await GitReadProcess.RunAsync(root, ["check-attr", "-z", "conflict-marker-size", "--", path], token);
        var fields = attributes.Split('\0');
        return fields.Length >= 3 && int.TryParse(fields[2], out var size) && size > 0 ? size : 7;
    }

    private static ConflictDocument Capture(string root, string path, int markerSize, CancellationToken token)
    {
        var state = RepositoryState.Token(root, [path], token);
        using var repo = new Repository(root);
        var conflict = repo.Index.Conflicts.FirstOrDefault(c => c.Ancestor?.Path == path || c.Ours?.Path == path || c.Theirs?.Path == path)
            ?? throw new IOException("该路径已无未合并索引条目，请刷新。");
        var operation = RepositoryOperation.Read(repo);
        string? limitation = null;
        ConflictSide Side(IndexEntry? entry, string label)
        {
            if (entry is null) return new(label, path, null, 0, "");
            string? text = null;
            if (entry.Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)) limitation = "符号链接或子模块冲突：请在外部工具处理，再标记解决。";
            else
            {
                var blob = repo.Lookup<Blob>(entry.Id);
                if (blob.Size > RepositoryReader.MaxTextBytes || blob.IsBinary) limitation = "二进制或超过 1 MiB：选择完整一侧，或外部处理后标记解决。";
                else
                {
                    using var input = blob.GetContentStream(); using var buffer = new MemoryStream(); input.CopyTo(buffer);
                    try { text = Decode(buffer.ToArray()); }
                    catch (DecoderFallbackException) { limitation = "非 UTF-8 文本：选择完整一侧，或外部处理后标记解决。"; }
                    if (text?.Split('\n').Any(line => line.Length > 20000) == true) limitation = "单行超过 20,000 字符，请整文件选择一侧或外部处理。";
                }
            }
            return new(label, entry.Path, entry.Id.Sha, (int)entry.Mode, text);
        }
        var ancestor = Side(conflict.Ancestor, "共同祖先（stage 1）");
        var left = Side(conflict.Ours, operation?.LeftLabel ?? "当前版本（ours / stage 2）");
        var right = Side(conflict.Theirs, operation?.RightLabel ?? "传入版本（theirs / stage 3）");
        if (left.Path != path || right.Path != path || ancestor.Path != path) limitation = "重命名 / 路径冲突：逐路径核对后选择完整一侧。";
        if (!left.Exists || !right.Exists) limitation = "删除 / 修改或重命名冲突：缺失的一侧表示删除；请核对路径后选择。";
        var full = RepositoryState.CheckedPath(root, path);
        byte[]? bytes = File.Exists(full) && new FileInfo(full).Length <= RepositoryReader.MaxTextBytes ? File.ReadAllBytes(full) : null;
        string? working = null;
        if (bytes is not null) try { working = Decode(bytes); } catch (DecoderFallbackException) { }
        var bom = bytes?.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) == true;
        var ending = bytes?.AsSpan().IndexOf("\r\n"u8) >= 0 ? "CRLF" : "LF";
        if (state != RepositoryState.Token(root, [path], token)) throw new IOException("冲突快照已变化，请重试。");
        return new(root, path, state, operation, ancestor, left, right, working, [], limitation, ending, bom,
            working is not null && !ContainsMarkers(working, markerSize), null, markerSize);
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) throw new DecoderFallbackException("Binary");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        return text.TrimStart('\uFEFF').Replace("\r\n", "\n");
    }

    public static bool ContainsMarkers(string text, int markerSize = 7)
    {
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < markerSize || line.IsEmpty || line[0] is not ('<' or '=' or '>' or '|')) continue;
            var count = 1; while (count < line.Length && line[count] == line[0]) count++;
            if (count == markerSize && (count == line.Length || line[count] == ' ')) return true;
        }
        return false;
    }

    public static async Task<MergeLayout> LayoutAsync(string left, string result, string right, CancellationToken token = default)
    {
        var middle = new DiffSide("result", "结果", result, true);
        var a = NativeDiff.CompareStructureAsync(new("left", "当前", left, true), middle, token);
        var b = NativeDiff.CompareStructureAsync(new("right", "传入", right, true), middle, token);
        await Task.WhenAll(a, b);
        return new(a.Result, b.Result);
    }

    public static async Task<(string Text, IReadOnlyList<MergeBlock> Blocks)> MergeAsync(string ancestor, string left, string right, CancellationToken token = default)
    {
        var inputs = new TemporaryGitInputs("merge", "base", "left", "right");
        var directory = inputs.DirectoryPath;
        var handedOff = false;
        var label = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var (name, text) in new[] { ("base", ancestor), ("left", left), ("right", right) })
                await File.WriteAllTextAsync(Path.Combine(directory, name), text, new UTF8Encoding(false), token);
            handedOff = true;
            var merged = await GitReadProcess.RunAsync(directory, ["merge-file", "--stdout", "--diff3", "--marker-size=32", "-L", "L" + label, "-L", "B" + label, "-L", "R" + label, "left", "base", "right"], token, allowMergeConflicts: true, afterExit: inputs.CleanupAsync);
            var open = new string('<', 32) + " L" + label + "\n";
            var middle = new string('|', 32) + " B" + label + "\n";
            var divider = new string('=', 32) + "\n";
            var close = new string('>', 32) + " R" + label;
            var result = new StringBuilder(); var blocks = new List<MergeBlock>(); var cursor = 0;
            while (merged.IndexOf(open, cursor, StringComparison.Ordinal) is var start && start >= 0)
            {
                result.Append(merged.AsSpan(cursor, start - cursor));
                var leftStart = start + open.Length; var baseMarker = merged.IndexOf(middle, leftStart, StringComparison.Ordinal);
                var baseStart = baseMarker + middle.Length; var separator = merged.IndexOf(divider, baseStart, StringComparison.Ordinal);
                var rightStart = separator + divider.Length; var end = merged.IndexOf(close, rightStart, StringComparison.Ordinal);
                if (baseMarker < 0 || separator < 0 || end < 0) throw new IOException("Git 三路合并标记不完整。");
                var after = end + close.Length; if (after < merged.Length && merged[after] == '\n') after++;
                string Chunk(int from, int to, string source)
                {
                    var chunk = merged[from..to];
                    if (after == merged.Length && !source.EndsWith('\n') && chunk.EndsWith('\n') && source.EndsWith(chunk[..^1], StringComparison.Ordinal)) return chunk[..^1];
                    return chunk;
                }
                var ours = Chunk(leftStart, baseMarker, left); var original = Chunk(baseStart, separator, ancestor); var theirs = Chunk(rightStart, end, right);
                blocks.Add(new(blocks.Count, result.Length, original.Length, ours, original, theirs));
                result.Append(original); cursor = after;
            }
            result.Append(merged.AsSpan(cursor));
            return (result.ToString(), blocks);
        }
        finally
        {
            if (!handedOff) await inputs.CleanupAsync().ConfigureAwait(false);
        }
    }
}
