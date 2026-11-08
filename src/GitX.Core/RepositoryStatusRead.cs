using LibGit2Sharp;

namespace GitX.Core;

internal readonly record struct StatusReadEntry(string Path, FileStatus State, string? IndexOldPath = null, string? WorktreeOldPath = null);

public sealed partial class RepositoryReader
{
    internal static IReadOnlyList<StatusReadEntry> ReconcileStatus(Repository repo, IReadOnlyList<StatusReadEntry> entries, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        HashSet<string>? deleted = null;
        foreach (var entry in entries)
            if ((entry.State & FileStatus.DeletedFromWorkdir) != 0)
                (deleted ??= new(StringComparer.Ordinal)).Add(entry.Path);
        if (deleted is null) return entries;

        var groups = new Dictionary<string, List<StatusReadEntry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if ((entry.State & FileStatus.NewInWorkdir) != 0 && deleted.Contains(entry.Path))
                groups.TryAdd(entry.Path, []);
        if (groups.Count == 0) return entries;
        foreach (var entry in entries)
            if (groups.TryGetValue(entry.Path, out var group)) group.Add(entry);

        using var verification = new Repository(repo.Info.Path);
        var replacements = new Dictionary<string, StatusReadEntry>(StringComparer.Ordinal);
        foreach (var (path, group) in groups)
        {
            token.ThrowIfCancellationRequested();
            var combined = group.Aggregate(FileStatus.Unaltered, (state, entry) => state | entry.State);
            if ((combined & ~(IndexFlags | FileStatus.DeletedFromWorkdir | FileStatus.NewInWorkdir)) != 0 ||
                group.Any(entry => entry.WorktreeOldPath is not null) ||
                verification.Index[path] is not { Mode: Mode.NonExecutableFile or Mode.ExecutableFile }) continue;
            string full;
            try { full = RepositoryState.CheckedPath(verification.Info.WorkingDirectory, path); }
            catch (IOException) { continue; }
            if (!File.Exists(full)) continue;

            // The Windows ARM64 native full iterator can report both deletion
            // and untracked for the same exact UTF-8 path. Do not merge status
            // labels: recheck a proven regular tracked file using the literal
            // single-path API, retaining the independently observed index state.
            var state = verification.RetrieveStatus(path);
            if ((state & ~(IndexFlags | FileStatus.ModifiedInWorkdir | FileStatus.TypeChangeInWorkdir)) != 0 ||
                (state & IndexFlags) != (combined & IndexFlags)) continue;
            replacements.Add(path, new(path, state, group.Select(entry => entry.IndexOldPath).FirstOrDefault(old => old is not null)));
        }
        if (replacements.Count == 0) return entries;

        var result = new List<StatusReadEntry>(entries.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!replacements.TryGetValue(entry.Path, out var replacement)) result.Add(entry);
            else if (emitted.Add(entry.Path) && replacement.State != FileStatus.Unaltered) result.Add(replacement);
        }
        return result;
    }
}
