using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitX.Core;

public static partial class NativeDiff
{
    // Immutable snapshots keep the line map, text and highlighting from different reads from mixing.
    // The app writes only its two temporary inputs, never repository objects or the index.
    public static async Task<(string Patch, SideBySideDiff Diff)> CompareAsync(DiffSide oldSide, DiffSide newSide, CancellationToken token, DiffWhitespace whitespace = DiffWhitespace.Exact)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GitX", "diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Line separators are metadata, not changed code. Keep the captured
            // sides byte-faithful for writes, but compare the same LF source lines
            // that Build and the editor display. Prefix each normalized source
            // line so a whitespace-only last line without a newline stays a
            // real line. The prefix exists only in temporary comparison input.
            string Input(string text)
            {
                if (whitespace == DiffWhitespace.Exact) return text.Replace("\r\n", "\n");
                return string.Join('\n', Lines(text).Select(line => "\u0001" + Normalize(line, whitespace).Text))
                    + (text.EndsWith('\n') ? "\n" : "");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "old.txt"), Input(oldSide.Text), new UTF8Encoding(false), token);
            await File.WriteAllTextAsync(Path.Combine(directory, "new.txt"), Input(newSide.Text), new UTF8Encoding(false), token);
            var patch = await GitReadProcess.RunAsync(directory,
                ["-c", "core.autocrlf=false", "diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-color",
                 "--diff-algorithm=myers", "--no-indent-heuristic", "--unified=0", "--", "old.txt", "new.txt"], token, allowDifferences: true);
            if (whitespace != DiffWhitespace.Exact)
                patch = patch.Replace("\n-\u0001", "\n-").Replace("\n+\u0001", "\n+").Replace("\n \u0001", "\n ");
            var diff = await Task.Run(() => Build(oldSide, newSide, patch, token, whitespace), token);
            return (patch, diff);
        }
        finally
        {
            // Delete only fixed files in the random directory created by this invocation.
            File.Delete(Path.Combine(directory, "old.txt"));
            File.Delete(Path.Combine(directory, "new.txt"));
            Directory.Delete(directory);
        }
    }

    public static SideBySideDiff Build(DiffSide oldSide, DiffSide newSide, string patch, CancellationToken token = default, DiffWhitespace whitespace = DiffWhitespace.Exact)
    {
        var oldLines = Lines(oldSide.Text);
        var newLines = Lines(newSide.Text);
        var blocks = new List<DiffBlock>();
        var oldEnd = 0;
        var newEnd = 0;
        var budget = 2_000_000;
        var coarse = false;
        foreach (Match match in HunkHeader().Matches(patch))
        {
            token.ThrowIfCancellationRequested();
            var oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            var newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            var oldStart = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - (oldCount == 0 ? 0 : 1);
            var newStart = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) - (newCount == 0 ? 0 : 1);
            if (oldStart < oldEnd || newStart < newEnd || oldStart + oldCount > oldLines.Length || newStart + newCount > newLines.Length
                || oldStart - oldEnd != newStart - newEnd)
                throw new InvalidDataException("Git diff 行范围与文档快照不一致。");
            VerifyContext(oldLines, newLines, oldEnd, newEnd, oldStart - oldEnd, whitespace);
            var rows = AlignLines(oldLines, newLines, oldStart, oldCount, newStart, newCount, token);
            var inline = new List<InlineChange>();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (row.OldLine is not { } oldLine || row.NewLine is not { } newLine) continue;
                var a = Normalize(oldLines[oldLine - 1], whitespace); var b = Normalize(newLines[newLine - 1], whitespace);
                var spans = Intraline(a.Text, b.Text, ref budget, ref coarse, token);
                var words = Intraline(a.Text, b.Text, ref budget, ref coarse, token, words: true);
                IReadOnlyList<InlineSpan> Map(IReadOnlyList<InlineSpan> source, int[] columns) => source.Select(span =>
                    new InlineSpan(columns[span.StartColumn - 1] + 1, columns[span.EndColumn - 2] + 2)).ToArray();
                inline.Add(new(oldLine, newLine, Map(spans.Old, a.Columns), Map(spans.New, b.Columns), Map(words.Old, a.Columns), Map(words.New, b.Columns)));
            }
            blocks.Add(new(oldStart, oldCount, newStart, newCount, inline, rows));
            oldEnd = oldStart + oldCount;
            newEnd = newStart + newCount;
        }
        if (oldLines.Length - oldEnd != newLines.Length - newEnd) throw new InvalidDataException("Git diff 未覆盖全部文本变化。");
        VerifyContext(oldLines, newLines, oldEnd, newEnd, oldLines.Length - oldEnd, whitespace);
        return new(oldSide, newSide, blocks, coarse);
    }

    public static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Split('\n');
        if (text.EndsWith('\n')) lines = lines[..^1];
        return lines.Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
    }

    private static (string Text, int[] Columns) Normalize(string text, DiffWhitespace whitespace)
    {
        var start = 0; var end = text.Length;
        if (whitespace == DiffWhitespace.Trim)
        { while (start < end && text[start] is ' ' or '\t') start++; while (end > start && text[end - 1] is ' ' or '\t') end--; }
        var columns = Enumerable.Range(start, end - start).Where(index => whitespace != DiffWhitespace.Ignore || text[index] is not (' ' or '\t')).ToArray();
        return (new string(columns.Select(index => text[index]).ToArray()), columns);
    }

    private static void VerifyContext(string[] oldLines, string[] newLines, int oldStart, int newStart, int count, DiffWhitespace whitespace)
    {
        for (var i = 0; i < count; i++)
            if (Normalize(oldLines[oldStart + i], whitespace).Text != Normalize(newLines[newStart + i], whitespace).Text) throw new InvalidDataException("Git diff 的未变区域与文档快照不一致。");
    }

    private static IReadOnlyList<DiffRow> AlignLines(string[] oldLines, string[] newLines,
        int oldStart, int oldCount, int newStart, int newCount, CancellationToken token)
    {
        // Bounded sequence alignment moves filler to inserted/deleted rows inside replacements.
        // Very large blocks retain ordinal pairing rather than allocating a quadratic matrix.
        if ((long)(oldCount + 1) * (newCount + 1) > 4096 || oldCount == 0 || newCount == 0)
            return Enumerable.Range(0, Math.Max(oldCount, newCount)).Select(i => new DiffRow(
                i < oldCount ? oldStart + i + 1 : null, i < newCount ? newStart + i + 1 : null)).ToArray();
        var oldFeatures = oldLines.Skip(oldStart).Take(oldCount).Select(LineFeatures).ToArray();
        var newFeatures = newLines.Skip(newStart).Take(newCount).Select(LineFeatures).ToArray();
        var scores = new double[oldCount + 1, newCount + 1];
        var steps = new byte[oldCount + 1, newCount + 1];
        for (var i = 1; i <= oldCount; i++) { scores[i, 0] = i; steps[i, 0] = 1; }
        for (var j = 1; j <= newCount; j++) { scores[0, j] = j; steps[0, j] = 2; }
        for (var i = 1; i <= oldCount; i++)
        {
            token.ThrowIfCancellationRequested();
            for (var j = 1; j <= newCount; j++)
            {
                var a = oldFeatures[i - 1]; var b = newFeatures[j - 1];
                var equal = oldLines[oldStart + i - 1].Trim() == newLines[newStart + j - 1].Trim();
                var similarity = equal ? 1 : a.Count + b.Count == 0 ? 0 : 2.0 * a.Count(b.Contains) / (a.Count + b.Count);
                var pair = scores[i - 1, j - 1] + 1.8 * (1 - similarity);
                var delete = scores[i - 1, j] + 1;
                var insert = scores[i, j - 1] + 1;
                scores[i, j] = Math.Min(pair, Math.Min(delete, insert));
                steps[i, j] = pair <= delete && pair <= insert ? (byte)0 : delete <= insert ? (byte)1 : (byte)2;
            }
        }
        var rows = new List<DiffRow>();
        var x = oldCount; var y = newCount;
        while (x > 0 || y > 0)
        {
            switch (steps[x, y])
            {
                case 0: rows.Add(new(oldStart + x--, newStart + y--)); break;
                case 1: rows.Add(new(oldStart + x--, null)); break;
                default: rows.Add(new(null, newStart + y--)); break;
            }
        }
        rows.Reverse();
        return rows;
    }

    private static HashSet<string> LineFeatures(string line)
    {
        var text = line.Trim();
        if (text.Length > 1024) text = text[..512] + text[^512..];
        return Enumerable.Range(0, Math.Max(0, text.Length - 1)).Select(i => text.Substring(i, 2)).ToHashSet(StringComparer.Ordinal);
    }

    private static (IReadOnlyList<InlineSpan> Old, IReadOnlyList<InlineSpan> New) Intraline(
        string oldText, string newText, ref int budget, ref bool coarse, CancellationToken token, bool words = false)
    {
        int[] Boundaries(string text)
        {
            var units = StringInfo.ParseCombiningCharacters(text);
            int Kind(int index) => char.IsLetterOrDigit(text, index) || text[index] is '_' or '$' ? 1 : char.IsWhiteSpace(text[index]) ? 2 : 0;
            return units.Where((index, i) => !words || i == 0 || Kind(index) == 0 || Kind(index) != Kind(units[i - 1])).Append(text.Length).ToArray();
        }
        var a = Boundaries(oldText);
        var b = Boundaries(newText);
        var n = a.Length - 1;
        var m = b.Length - 1;
        bool Equal(int i, int j) => oldText.AsSpan(a[i], a[i + 1] - a[i]).SequenceEqual(newText.AsSpan(b[j], b[j + 1] - b[j]));
        var prefix = 0;
        while (prefix < n && prefix < m && Equal(prefix, prefix)) prefix++;
        var suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && Equal(n - suffix - 1, m - suffix - 1)) suffix++;
        var lengthA = n - prefix - suffix;
        var lengthB = m - prefix - suffix;
        var oldSpans = new List<InlineSpan>();
        var newSpans = new List<InlineSpan>();
        void Add(int startA, int endA, int startB, int endB)
        {
            if (endA > startA) oldSpans.Add(new(a[startA] + 1, a[endA] + 1));
            if (endB > startB) newSpans.Add(new(b[startB] + 1, b[endB] + 1));
        }
        if (lengthA == 0 || lengthB == 0) { Add(prefix, n - suffix, prefix, m - suffix); return (oldSpans, newSpans); }
        var cells = (long)(lengthA + 1) * (lengthB + 1);
        if (cells > 262144 || cells > budget)
        {
            coarse = true;
            Add(prefix, n - suffix, prefix, m - suffix);
            return (oldSpans, newSpans);
        }
        budget -= (int)cells;
        var width = lengthB + 1;
        var lcs = new ushort[(int)cells];
        for (var i = lengthA - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            for (var j = lengthB - 1; j >= 0; j--)
                lcs[i * width + j] = Equal(i + prefix, j + prefix) ? (ushort)(lcs[(i + 1) * width + j + 1] + 1)
                    : Math.Max(lcs[(i + 1) * width + j], lcs[i * width + j + 1]);
        }
        var x = 0; var y = 0; var startX = 0; var startY = 0;
        while (x < lengthA || y < lengthB)
        {
            if (x < lengthA && y < lengthB && Equal(x + prefix, y + prefix))
            {
                Add(startX + prefix, x + prefix, startY + prefix, y + prefix);
                x++; y++; startX = x; startY = y;
            }
            else if (x < lengthA && (y == lengthB || lcs[(x + 1) * width + y] >= lcs[x * width + y + 1])) x++;
            else y++;
        }
        Add(startX + prefix, x + prefix, startY + prefix, y + prefix);
        return (oldSpans, newSpans);
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Multiline)]
    private static partial Regex HunkHeader();
}
