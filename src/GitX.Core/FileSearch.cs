using System.Globalization;
using System.IO.Enumeration;

namespace GitX.Core;

// Filename navigation only. Text searches keep their literal/regex semantics.
// The inventory belongs to the caller's repository snapshot; this builds no index.
internal static class FileSearch
{
    private readonly record struct Candidate(string Path, int Tier, int Recent, int Cost);
    private static readonly IComparer<Candidate> Order = Comparer<Candidate>.Create((a, b) =>
    {
        var value = a.Tier.CompareTo(b.Tier);
        if (value == 0) value = a.Recent.CompareTo(b.Recent);
        if (value == 0) value = a.Cost.CompareTo(b.Cost);
        if (value == 0) value = a.Path.Length.CompareTo(b.Path.Length);
        if (value == 0) value = StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
        return value != 0 ? value : StringComparer.Ordinal.Compare(a.Path, b.Path);
    });
    private static readonly IComparer<Candidate> WorstFirst = Comparer<Candidate>.Create((a, b) => Order.Compare(b, a));

    internal static CodeSearchPage Find(IReadOnlyList<string> paths, CodeSearchQuery query,
        IReadOnlyList<string>? recentPaths, CancellationToken token)
    {
        var text = query.Text.Trim().Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];
        var line = 1; var column = 1;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && Positive(text.AsSpan(colon + 1), out var last))
        {
            text = text[..colon]; line = last;
            colon = text.LastIndexOf(':');
            if (colon > 0 && Positive(text.AsSpan(colon + 1), out var first))
            { text = text[..colon]; line = first; column = last; }
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new([], paths.Count, 0, false);
        var masks = query.FileMask.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var recent = new Dictionary<string, int>(StringComparer.Ordinal);
        if (recentPaths is not null)
            for (var i = 0; i < Math.Min(64, recentPaths.Count); i++) recent.TryAdd(recentPaths[i], i);
        var best = new PriorityQueue<Candidate, Candidate>(WorstFirst);
        var count = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var name = path.AsSpan(path.LastIndexOf('/') + 1);
            if (masks.Length > 0 && !masks.Any(mask => FileSystemName.MatchesSimpleExpression(mask.Replace('\\', '/'),
                mask.Contains('/') || mask.Contains('\\') ? path : System.IO.Path.GetFileName(path), true))) continue;
            var tier = 0; var cost = 0; var matched = true;
            foreach (var word in words)
            {
                var nameCost = Match(name, word, token);
                if (nameCost >= 0) { tier = Math.Max(tier, 4); cost += nameCost; }
                else
                {
                    var pathCost = Match(path, word, token);
                    if (pathCost < 0) { matched = false; break; }
                    tier = 5; cost += pathCost;
                }
            }
            if (!matched) continue;
            if (words.Length == 1)
            {
                var needle = words[0].AsSpan();
                var dot = name.LastIndexOf('.');
                if (name.Equals(needle, StringComparison.OrdinalIgnoreCase)) tier = 0;
                else if (dot > 0 && name[..dot].Equals(needle, StringComparison.OrdinalIgnoreCase)) tier = 1;
                else if (name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) tier = 2;
                else if (name.Contains(needle, StringComparison.OrdinalIgnoreCase)) tier = 3;
            }
            count++;
            var candidate = new Candidate(path, tier, recent.GetValueOrDefault(path, int.MaxValue), cost);
            if (best.Count < RepositoryReader.MaxSearchResults) best.Enqueue(candidate, candidate);
            else if (Order.Compare(candidate, best.Peek()) < 0) { best.Dequeue(); best.Enqueue(candidate, candidate); }
        }
        var ordered = best.UnorderedItems.Select(item => item.Element).ToArray();
        Array.Sort(ordered, Order);
        return new(ordered.Select(item => new CodeSearchMatch(item.Path, line, column, 0, "")).ToArray(),
            paths.Count, 0, count > RepositoryReader.MaxSearchResults);
    }

    private static bool Positive(ReadOnlySpan<char> text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static int Match(ReadOnlySpan<char> value, ReadOnlySpan<char> query, CancellationToken token)
    {
        var at = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return at;
        var next = 0; var first = -1; var last = 0; var boundaries = 0;
        for (var i = 0; i < value.Length && next < query.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            if (char.ToUpperInvariant(value[i]) != char.ToUpperInvariant(query[next])) continue;
            if (first < 0) first = i;
            if (i == 0 || !char.IsLetterOrDigit(value[i - 1]) || char.IsLower(value[i - 1]) && char.IsUpper(value[i])) boundaries++;
            last = i; next++;
        }
        return next == query.Length ? 1000 + (last - first + 1 - query.Length) * 4 + first - boundaries : -1;
    }
}
