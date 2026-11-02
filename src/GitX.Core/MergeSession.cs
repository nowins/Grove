namespace GitX.Core;

public sealed partial class ConflictReader
{
    public static Task<IReadOnlyList<ConflictFile>> ListAsync(string root, CancellationToken token = default) => Task.Run<IReadOnlyList<ConflictFile>>(() =>
    {
        using var repo = new LibGit2Sharp.Repository(root);
        var files = new List<ConflictFile>();
        foreach (var conflict in repo.Index.Conflicts)
        {
            token.ThrowIfCancellationRequested();
            string Status(LibGit2Sharp.IndexEntry? entry) => entry is null ? "删除" : conflict.Ancestor is null ? "新增" : "修改";
            foreach (var path in new[] { conflict.Ancestor?.Path, conflict.Ours?.Path, conflict.Theirs?.Path }.OfType<string>().Distinct(StringComparer.Ordinal))
                files.Add(new(path, Status(conflict.Ours), Status(conflict.Theirs)));
        }
        return files.DistinctBy(f => f.Path, StringComparer.Ordinal).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }, token);

    // The editor starts at the ancestor, with explicit decisions for clean
    // changes as well as conflicts. Git remains the authority on overlapping edits.
    public static async Task<ConflictDocument> PrepareSessionAsync(ConflictDocument document, CancellationToken token = default)
    {
        if (!document.IsText || document.ExistingResult) return document;
        var ancestor = document.Base.Text!; var left = document.Left.Text!; var right = document.Right.Text!;
        var baseline = new DiffSide(document.Path, "共同祖先", ancestor, document.Base.Exists);
        var a = NativeDiff.CompareAsync(baseline, new(document.Path, "当前", left, true), token);
        var b = NativeDiff.CompareAsync(baseline, new(document.Path, "传入", right, true), token);
        await Task.WhenAll(a, b);
        var changes = a.Result.Diff.Blocks.Concat(b.Result.Diff.Blocks).OrderBy(c => c.OldStart).ToArray();
        var spans = new List<(int Start, int End)>();
        foreach (var change in changes)
        {
            var end = change.OldStart + change.OldCount;
            if (spans.Count > 0 && change.OldStart <= spans[^1].End) spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, end));
            else spans.Add((change.OldStart, end));
        }
        static int[] Offsets(string text)
        {
            var result = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') result.Add(i + 1);
            if (result[^1] != text.Length) result.Add(text.Length);
            return result.ToArray();
        }
        var baseOffsets = Offsets(ancestor); var leftOffsets = Offsets(left); var rightOffsets = Offsets(right);
        (int Start, int End) Project(IReadOnlyList<DiffBlock> edits, int start, int end) =>
            (start + edits.Where(c => c.OldStart < start).Sum(c => c.NewCount - c.OldCount), end + edits.Where(c => c.OldStart <= end).Sum(c => c.NewCount - c.OldCount));
        var blocks = new MergeBlock[spans.Count];
        // Each span is independent. Keep Git as the merge authority, with a
        // bounded number of read-only processes and no span work on the UI thread.
        await Parallel.ForEachAsync(Enumerable.Range(0, spans.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            var (start, end) = spans[index];
            var l = Project(a.Result.Diff.Blocks, start, end); var r = Project(b.Result.Diff.Blocks, start, end);
            var original = ancestor[baseOffsets[start]..baseOffsets[end]];
            var ours = left[leftOffsets[l.Start]..leftOffsets[l.End]]; var theirs = right[rightOffsets[r.Start]..rightOffsets[r.End]];
            var leftChanged = ours != original; var rightChanged = theirs != original;
            var conflict = false; string automatic;
            if (ours == theirs) automatic = ours;
            else if (!leftChanged) automatic = theirs;
            else if (!rightChanged) automatic = ours;
            else
            {
                var merge = await MergeAsync(original, ours, theirs, cancellation);
                automatic = merge.Text; conflict = merge.Blocks.Count > 0;
            }
            blocks[index] = new(index, baseOffsets[start], original.Length, ours, original, theirs, conflict,
                leftChanged, rightChanged, automatic, l.Start, l.End - l.Start, r.Start, r.End - r.Start);
        });
        var layout = await LayoutAsync(left, ancestor, right, token);
        if (document.Token != await Task.Run(() => RepositoryState.Token(document.Root, [document.Path], token), token)) throw new IOException("冲突快照已变化，请重新打开。");
        return document with { Result = ancestor, Blocks = blocks, Layout = layout };
    }
}
