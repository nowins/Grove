namespace GitX.Core;

// Watcher paths are invalidation hints, never evidence that a Git write is safe.
// Only ordinary content changes may be narrowed; structural/metadata changes,
// overflow and explicit refresh retain the full repository reconciliation path.
public sealed class RepositoryChangeSet
{
    public static RepositoryChangeSet Full { get; } = new(true, []);
    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public bool IsFull { get; }
    public IReadOnlyList<string> Paths { get; }

    private RepositoryChangeSet(bool full, string[] paths)
    { IsFull = full; Paths = Array.AsReadOnly(paths); }

    public static RepositoryChangeSet Content(IEnumerable<string> paths)
    {
        var result = new HashSet<string>(Comparer);
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains('\0') ||
                path.Split('/').Any(part => part is "" or "." or ".." ||
                    part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    IsConfigurationName(part))) return Full;
            result.Add(path);
            if (result.Count > 64) return Full;
        }
        return result.Count == 0 ? Full : new(false, result.Order(StringComparer.Ordinal).ToArray());
    }

    public RepositoryChangeSet Merge(RepositoryChangeSet next) => IsFull || next.IsFull ? Full : Content(Paths.Concat(next.Paths));

    internal static bool IsConfigurationName(string name) => name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) || name.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".mailmap", StringComparison.OrdinalIgnoreCase);

    public bool Touches(string path) => IsFull || Paths.Any(changed => string.Equals(path, changed, Comparison) ||
        path.StartsWith(changed + "/", Comparison) || changed.StartsWith(path + "/", Comparison));
}

public sealed record RepositoryRefreshResult(RepositorySnapshot Snapshot, RepositoryChangeSet Changes);
