using GitX.Core;
namespace GitX.Desktop.Services;

internal sealed record HistoryReferenceChoice(string Key, string Name, string Group, HistoryReference? Reference = null,
    bool IsHeading = false, bool Selected = false, bool Favorite = false)
{
    public bool Selectable => !IsHeading;
    public bool CanFavorite => Reference is not null;
    public string KindLabel => Reference?.Kind switch { "local" => "本地", "remote" => "远端", "tag" => "标签", _ => "" };
    public string Detail => Reference is null ? "" : KindLabel + " · " + Key;
}

// Sort once per popup; filtering never resolves refs or runs Git, and retains
// exact canonical identities even when branch/tag display names coincide.
internal sealed class HistoryReferenceChoices
{
    private readonly HistoryReference[] _references;
    private readonly Dictionary<string, HistoryReference> _byKey;
    public HistoryReferenceChoices(IEnumerable<HistoryReference> references)
    {
        _references = references.Where(r => HistoryReferencePreferenceStore.IsReference(r.CanonicalName))
            .DistinctBy(r => r.CanonicalName, StringComparer.Ordinal).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.CanonicalName, StringComparer.Ordinal).ToArray();
        _byKey = _references.ToDictionary(r => r.CanonicalName, StringComparer.Ordinal);
    }
    public IReadOnlyList<HistoryReferenceChoice> Build(HistoryReferencePreferences preferences, string? selected, string query)
    {
        var words = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Match(string name, string key) => words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase) || key.Contains(w, StringComparison.OrdinalIgnoreCase));
        var favorites = preferences.Favorites.ToHashSet(StringComparer.Ordinal); var rows = new List<HistoryReferenceChoice>();
        if (Match("全部提交", "all")) rows.Add(new("all", "全部提交", "范围", Selected: selected is null));
        if (Match("HEAD（当前检出）", "HEAD")) rows.Add(new("HEAD", "HEAD（当前检出）", "范围", Selected: selected == "HEAD"));
        void Group(string label, IEnumerable<HistoryReference> candidates)
        {
            var filtered = candidates.Where(r => Match(r.Name, r.CanonicalName)).ToArray();
            if (filtered.Length == 0) return;
            rows.Add(new("group:" + label, label, label, IsHeading: true));
            rows.AddRange(filtered.Select(r => new HistoryReferenceChoice(r.CanonicalName, r.Name, label, r,
                Selected: selected == r.CanonicalName, Favorite: favorites.Contains(r.CanonicalName))));
        }
        IEnumerable<HistoryReference> Resolve(IEnumerable<string> keys) => keys.Distinct(StringComparer.Ordinal).Select(key => _byKey.GetValueOrDefault(key)).OfType<HistoryReference>();
        Group("收藏", Resolve(preferences.Favorites)); Group("最近使用", Resolve(preferences.Recent));
        foreach (var (kind,label) in new[] { ("local","本地分支"), ("remote","远端分支"), ("tag","标签") }) Group(label, _references.Where(r => r.Kind == kind));
        return rows;
    }
}
