using LibGit2Sharp;

namespace GitX.Core;

// File events are hints. Callers always reread Git; nothing here infers repository state.
public sealed class RepositoryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly string _root;
    private readonly Action _changed;
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private bool _disposed, _force, _checking;

    public RepositoryWatcher(string root, Action changed)
    {
        _root = root; _changed = changed;
        _timer = new Timer(_ => CheckBatch(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            using var repo = new Repository(root);
            Add(root, false);
            var gitDirectory = repo.Info.Path;
            Add(gitDirectory, true);
            var commonFile = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(commonFile))
            {
                var common = Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(commonFile).Trim()));
                if (!string.Equals(common.TrimEnd(Path.DirectorySeparatorChar), gitDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) Add(common, true);
            }
        }
        catch { Dispose(); throw; }
    }

    private void Add(string directory, bool metadata)
    {
        var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        void Changed(object sender, FileSystemEventArgs e)
        {
            var name = (e.Name ?? "").Replace('\\', '/');
            if (metadata)
            {
                if (!(name is "index" or "HEAD" or "packed-refs" or "config" or "info/exclude" or "MERGE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD" or "REBASE_HEAD" or "ORIG_HEAD"
                    || name.StartsWith("refs/", StringComparison.Ordinal) || name.StartsWith("rebase-", StringComparison.Ordinal) || name.StartsWith("sequencer", StringComparison.Ordinal))) return;
                if (name.EndsWith(".lock", StringComparison.Ordinal)) return;
            }
            else if (name.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)) return;
            // Creating an ignored child can also change the parent's mtime.
            // File events carry the actual content change; directory rename /
            // delete / create events must still be checked for tracked paths.
            else if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath)) return;
            Signal(metadata ? null : name);
        }
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
        watcher.Renamed += (s, e) => { Changed(s, e); if (!metadata) Signal(e.OldName?.Replace('\\', '/')); };
        watcher.Error += (_, _) => Signal(null); // Overflow also requires a full Git refresh.
        _watchers.Add(watcher); watcher.EnableRaisingEvents = true;
    }

    private void Signal(string? path)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(path) || path == ".git" || path.Split('/').Last() == ".gitignore" || _paths.Count >= 2048) _force = true;
            else if (!_force) _paths.Add(path);
            _timer.Change(350, Timeout.Infinite);
        }
    }
    private void CheckBatch()
    {
        string[] paths; bool refresh;
        lock (_gate)
        {
            if (_disposed) return;
            if (_checking) { _timer.Change(350, Timeout.Infinite); return; }
            _checking = true; refresh = _force; _force = false; paths = _paths.ToArray(); _paths.Clear();
        }
        try
        {
            // Timer runs off the UI thread. Ignore rules alone cannot suppress
            // tracked files (or a renamed/deleted directory containing them).
            if (!refresh && paths.Length > 0)
            {
                using var repo = new Repository(_root);
                var tracked = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var entry in repo.Index)
                {
                    var path = entry.Path; tracked.Add(path);
                    for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/'))
                    { path = path[..slash]; tracked.Add(path); }
                }
                refresh = paths.Any(path => tracked.Contains(path) || !repo.Ignore.IsPathIgnored(
                    Directory.Exists(Path.Combine(_root, path)) ? path.TrimEnd('/') + "/" : path));
            }
        }
        catch { refresh = true; } // An uncertain event always falls back to Git.
        finally
        {
            lock (_gate)
            {
                _checking = false;
                if (!_disposed)
                {
                    if (refresh) _changed();
                    if (_force || _paths.Count > 0) _timer.Change(350, Timeout.Infinite);
                }
            }
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _timer.Dispose(); }
        foreach (var watcher in _watchers) watcher.Dispose();
    }
}
