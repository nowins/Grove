namespace GitX.Core;

public sealed record StageSelection(int? Block, int Side = 1, int StartLine = 0, int EndLine = 0);
public sealed record ChangeLine(int Side, int Line)
{
    public static ChangeLine For(DiffRow row) => row.NewLine is { } line ? new(1, line) : new(0, row.OldLine!.Value);
}

public static class PartialStage
{
    public static string RevertBlock(SideBySideDiff diff, int block)
    {
        if (block < 0 || block >= diff.Blocks.Count) throw new IOException("变更块已失效，请刷新后重试。");
        var current = diff.Blocks[block];
        var working = Lines(diff.New.Text);
        var original = Lines(diff.Old.Text).Skip(current.OldStart).Take(current.OldCount);
        // Restored rows use the worktree's separators; all unselected rows keep
        // their exact bytes, including mixed EOL and an unterminated last line.
        var newline = (diff.New.Text.Length == 0 ? diff.Old : diff.New).PreferredNewline;
        var restored = original.Select(line => line.EndsWith("\r\n", StringComparison.Ordinal) ? line[..^2] + newline : line.EndsWith('\n') ? line[..^1] + newline : line);
        var output = working.Take(current.NewStart).Concat(restored).Concat(working.Skip(current.NewStart + current.NewCount)).ToList();
        for (var i = 0; i < output.Count - 1; i++) if (!output[i].EndsWith('\n')) output[i] += newline;
        return string.Concat(output);
    }

    public static (string Text, bool Exists) Apply(SideBySideDiff diff, StageSelection selection)
    {
        if (selection.Block is { } block && (block < 0 || block >= diff.Blocks.Count)) throw new IOException("变更块已失效，请刷新后重试。");
        if (selection.Block is null && (selection.Side is < 0 or > 1 || selection.StartLine < 1 || selection.EndLine < selection.StartLine)) throw new IOException("请选择变更行。");
        var selectedRows = diff.Blocks.SelectMany((block, index) => block.Rows.Where(row => selection.Block is { } target ? target == index
            : (selection.Side == 0 ? row.OldLine : row.NewLine) >= selection.StartLine && (selection.Side == 0 ? row.OldLine : row.NewLine) <= selection.EndLine)).Select(ChangeLine.For).ToHashSet();
        return ApplyRows(diff, selectedRows);
    }

    public static (string Text, bool Exists) ApplyRows(SideBySideDiff diff, IReadOnlySet<ChangeLine> selectedRows)
    {
        if (selectedRows.Count == 0 || selectedRows.Except(diff.Blocks.SelectMany(block => block.Rows).Select(ChangeLine.For)).Any())
            throw new IOException("所选变更行已失效，请刷新后重新选择。");
        var oldLines = Lines(diff.Old.Text); var newLines = Lines(diff.New.Text);
        var output = new List<string>(); var oldEnd = 0; var changed = false;
        for (var index = 0; index < diff.Blocks.Count; index++)
        {
            var current = diff.Blocks[index];
            output.AddRange(oldLines.Skip(oldEnd).Take(current.OldStart - oldEnd));
            foreach (var row in current.Rows)
            {
                var selected = selectedRows.Contains(ChangeLine.For(row));
                if (selected)
                {
                    changed = true;
                    if (row.NewLine is { } added) output.Add(newLines[added - 1]);
                }
                else if (row.OldLine is { } kept) output.Add(oldLines[kept - 1]);
            }
            oldEnd = current.OldStart + current.OldCount;
        }
        output.AddRange(oldLines.Skip(oldEnd));
        if (!changed) throw new IOException("选区没有变更行。删除行请从旧侧选择，或暂存整个变更块。");
        var newline = diff.New.PreferredNewline;
        // An insertion after a formerly unterminated line needs a separator.
        for (var i = 0; i < output.Count - 1; i++) if (!output[i].EndsWith('\n')) output[i] += newline;
        return (string.Concat(output), output.Count > 0 || diff.New.Exists);
    }

    private static List<string> Lines(string text)
    {
        var result = new List<string>(); var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) { result.Add(text[start..]); break; }
            result.Add(text[start..(end + 1)]); start = end + 1;
        }
        return result;
    }
}
