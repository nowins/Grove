using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IReadOnlyList<NavigationEntry> _historyEntries = [];
    public IReadOnlyList<NavigationEntry> HistoryEntries => _historyEntries;

    public FileHistoryTarget? CurrentProjectFile => Root is { } root && _document is { IsNotice: false, Path.Length: > 0 } document ? new(root, document.Path) : null;

    // Tool-window lists have their own lifetime. Editor tabs own documents.
    public IReadOnlyList<NavigationEntry> NavigationEntries(int tool)
    {
        if (_snapshot is null) return [];
        if (tool == 2) return _historyEntries;
        if (tool == 1) return _snapshot.Changes.GroupBy(change => change.Path).Select(group =>
        {
            var change = group.First(); var conflict = group.Any(item => item.Kind == ComparisonKind.Conflict);
            return new NavigationEntry(change.Path, conflict ? "冲突" : "HEAD → 当前版本", conflict ? ComparisonKind.Conflict : ComparisonKind.LocalChanges,
                group.Select(item => item.OldPath).FirstOrDefault(path => path is not null), Status: change.Status);
        }).ToArray();
        var statuses = _snapshot.Changes.GroupBy(c => c.Path).ToDictionary(g => g.Key, g => g.First().Status);
        return _snapshot.Files.Select(f => new NavigationEntry(f.Path, f.Path, ComparisonKind.WorkingFile, Status: statuses.GetValueOrDefault(f.Path, ""))).ToArray();
    }

    private void SetHistoryEntries(IReadOnlyList<NavigationEntry> entries)
    {
        _historyEntries = entries; Changed(nameof(HistoryEntries));
        if (_mode == 2) { _allEntries = entries; FilterEntries(); }
    }

    private void SetEditorContext(int mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        // Rebuilding a tool list must not close or select an editor document.
        _updatingEntries = true;
        try { BuildEntries(); } finally { _updatingEntries = false; }
        Changed(nameof(Mode)); Changed(nameof(IsChanges)); Changed(nameof(IsHistory)); Changed(nameof(NavigationTitle));
        NavigationChanged?.Invoke(); WriteStateChanged();
    }

    public Task OpenEditorEntryAsync(NavigationEntry entry)
    {
        if (_closed || _snapshot is null) return Task.CompletedTask;
        SetEditorContext(EditorContext(entry));
        OpenDocumentTab(entry); _selectedEntry = entry; Changed(nameof(SelectedEntry));
        return SelectEntryAsync(entry);
    }

    private static int EditorContext(NavigationEntry entry) => entry.Kind == ComparisonKind.WorkingFile ? 0 :
        entry.Kind is ComparisonKind.LocalChanges or ComparisonKind.Worktree or ComparisonKind.Index or ComparisonKind.Conflict ? 1 : 2;
}
