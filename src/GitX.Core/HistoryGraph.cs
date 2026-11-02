namespace GitX.Core;

public sealed record HistoryEdge(int From, int To, int Color, bool Upper);
public sealed record HistoryGraphRow(int Lane, int Color, int Width, bool Merge, IReadOnlyList<HistoryEdge> Edges);

// Pending parent identities, not branch names or timestamps, determine graph topology.
// Keeping this state with the history cursor preserves lanes across pages.
public sealed class HistoryGraph
{
    private readonly List<(string Sha, int Color)> _lanes = [];
    private int _color;
    public HistoryGraphRow Append(string sha, IReadOnlyList<string> parents)
    {
        var lane = _lanes.FindIndex(x => x.Sha == sha);
        if (lane < 0) { lane = _lanes.Count; _lanes.Add((sha, _color++)); }
        var before = _lanes.ToArray(); var color = before[lane].Color;
        _lanes.RemoveAt(lane);
        var insert = Math.Min(lane, _lanes.Count);
        foreach (var parent in parents.Distinct())
            if (!_lanes.Any(x => x.Sha == parent)) _lanes.Insert(insert++, (parent, parent == parents[0] ? color : _color++));
        var edges = new List<HistoryEdge>();
        // A newly introduced tip has no edge arriving from the preceding row.
        if (_seen.Remove(sha)) edges.Add(new(lane, lane, color, true));
        foreach (var (item, index) in before.Select((x, i) => (x, i)))
            if (index != lane)
            {
                edges.Add(new(index, index, item.Color, true));
                edges.Add(new(index, _lanes.FindIndex(x => x.Sha == item.Sha), item.Color, false));
            }
        foreach (var parent in parents.Distinct())
        {
            var target = _lanes.FindIndex(x => x.Sha == parent);
            edges.Add(new(lane, target, _lanes[target].Color, false)); _seen.Add(parent);
        }
        return new(lane, color, Math.Max(2, Math.Max(before.Length, _lanes.Count)), parents.Count > 1, edges);
    }
    private readonly HashSet<string> _seen = [];
}
