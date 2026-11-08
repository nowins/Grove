using LibGit2Sharp;

namespace GitX.Core;

public sealed partial class RepositoryReader
{
    public async Task<RepositoryRefreshResult> RefreshAsync(RepositorySnapshot previous, RepositoryChangeSet changes, CancellationToken token = default)
    {
        if (!changes.IsFull && !previous.FilesTruncated && previous.IndexToken is not null && previous.Operation is null)
        {
            var updated = await ReadWork.Cpu.Run(() => RefreshContent(previous, changes, token), token, ReadPriority.Background).ConfigureAwait(false);
            if (updated is not null) return new(updated, changes);
        }
        return new(await OpenAsync(previous.Root, token, ReadPriority.Background).ConfigureAwait(false), RepositoryChangeSet.Full);
    }

    private static RepositorySnapshot? RefreshContent(RepositorySnapshot previous, RepositoryChangeSet changes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (RepositoryState.Token(previous.Root, [], token) != previous.IndexToken) return null;
        using var repo = new Repository(previous.Root);
        var updated = previous.Changes.ToList();
        foreach (var path in changes.Paths)
        {
            token.ThrowIfCancellationRequested();
            // New/deleted paths, gitlinks, symlinks and ambiguous/conflicted
            // entries need the complete scan. RetrieveStatus(string) is a
            // literal single-path query, not a glob/pathspec.
            if (!previous.Files.Any(file => file.Path == path) ||
                repo.Index[path] is not { Mode: Mode.NonExecutableFile or Mode.ExecutableFile } ||
                previous.Changes.Any(change => change.Path == path && change.Kind == ComparisonKind.Conflict)) return null;
            string full;
            try { full = RepositoryState.CheckedPath(previous.Root, path); }
            catch (IOException) { return null; }
            if (!File.Exists(full)) return null;
            var state = repo.RetrieveStatus(path);
            if ((state & ~(IndexFlags | FileStatus.ModifiedInWorkdir | FileStatus.TypeChangeInWorkdir)) != 0) return null;
            updated.RemoveAll(change => change.Path == path && change.Kind == ComparisonKind.Worktree);
            if ((state & WorkFlags) != 0) updated.Add(new(path, ComparisonKind.Worktree, StatusLabel(state, false)));
        }
        // Includes HEAD, index and operation state. A delayed metadata event
        // cannot let a path-only read carry an obsolete write context forward.
        if (RepositoryState.Token(previous.Root, [], token) != previous.IndexToken) return null;
        var ordered = updated.OrderBy(change => change.Kind).ThenBy(change => change.Path, StringComparer.Ordinal).ToArray();
        return ordered.SequenceEqual(previous.Changes) ? previous : previous with { Changes = ordered };
    }
}
