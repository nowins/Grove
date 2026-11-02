using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed class FileNavigationRow(string Key, string Path, string Name, int Depth,
    NavigationEntry? Entry = null, bool IsDirectory = false, bool IsExpanded = false, bool IsGroup = false)
    : INotifyPropertyChanged
{
    public string Key { get; } = Key;
    public string Path { get; } = Path;
    public string Name { get; private set; } = Name;
    public int Depth { get; private set; } = Depth;
    public NavigationEntry? Entry { get; private set; } = Entry;
    public bool IsDirectory { get; } = IsDirectory;
    public bool IsExpanded { get; private set; } = IsExpanded;
    public bool IsGroup { get; } = IsGroup;
    public bool CanExpand => IsDirectory || IsGroup;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(FileNavigationRow next)
    {
        if (Name == next.Name && Depth == next.Depth && Entry == next.Entry && IsExpanded == next.IsExpanded) return;
        Name = next.Name; Depth = next.Depth; Entry = next.Entry; IsExpanded = next.IsExpanded;
        PropertyChanged?.Invoke(this, new(null));
    }
    public bool IsFile => Entry is not null;
    public bool CanInclude => Entry?.Kind == ComparisonKind.LocalChanges || IsGroup && Key == "group:LocalChanges";
    public bool? Included { get; private set; } = false;
    public bool IncludeEnabled { get; private set; } = true;
    public void SetIncluded(bool? value, bool enabled = true)
    { if (Included == value && IncludeEnabled == enabled) return; Included = value; IncludeEnabled = enabled; PropertyChanged?.Invoke(this, new(null)); }
    public double Indent => Depth * 16;
    public double ChevronWidth => CanExpand || Depth > 0 && Entry?.Kind != ComparisonKind.LocalChanges ? 14 : 0;
    public string ParentLabel => IsFile && (Depth == 0 || Entry?.Kind is ComparisonKind.LocalChanges or ComparisonKind.Index or ComparisonKind.Worktree or ComparisonKind.Conflict) ? (Entry?.Kind == ComparisonKind.LocalChanges ? System.IO.Path.GetDirectoryName(Path) : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))) ?? "" : "";
    public bool IsJava => FileType == "JV";
    public string TypeGlyph => FileType == "JV" ? "" : FileType;
    public string Tooltip => Entry is null ? Path : $"{Path}\n{Entry.Subtitle}";
    private string DirectoryLabel => Path.Length == 0 ? Name : Path;
    public string AccessibleName => IsGroup ? Name : IsDirectory ? $"目录 {DirectoryLabel}，{(IsExpanded ? "已展开" : "已收起")}" : $"{Path}，{Entry?.StageLabel} {Entry?.Status}".TrimEnd(' ', '，');
    public string ToggleName => $"{(IsExpanded ? "收起" : "展开")}{(IsGroup ? "分组" : "目录")} {DirectoryLabel}";
    public string FileType => Entry?.FileType ?? "";
    public string StatusCode => Entry?.StatusCode ?? "";
    public bool IsSource => Entry?.IsSource == true;
    public bool IsMarkdown => FileType == "MD";
    public bool IsData => FileType is "SQL" or "{}" or "<>";
    public bool IsAdded => Entry?.IsAdded == true;
    public bool IsDeleted => Entry?.IsDeleted == true;
    public bool IsModified => Entry?.IsModified == true;
    public bool IsConflict => Entry?.IsConflict == true;
}

// A virtualized ListBox renders only the visible tree rows. Git entries remain
// unchanged: expanding a directory never creates a repository/document request.
public sealed class FileNavigation
{
    private sealed class DirectoryNode(string path, string name, int depth)
    {
        public readonly string Path = path, Name = name;
        public readonly int Depth = depth;
        public readonly Dictionary<string, DirectoryNode> Directories = new(StringComparer.Ordinal);
        public readonly List<NavigationEntry> Files = [];
    }
    private sealed class RowCollection : ObservableCollection<FileNavigationRow>
    {
        public void Update(IReadOnlyList<FileNavigationRow> next)
        {
            // Status and group counts change without replacing the selected row.
            var previous = this.ToDictionary(row => row.Key, StringComparer.Ordinal);
            next = next.Select(row =>
            {
                if (!previous.TryGetValue(row.Key, out var current)) return row;
                current.Update(row); return current;
            }).ToArray();
            var prefix = 0;
            while (prefix < Count && prefix < next.Count && this[prefix] == next[prefix]) prefix++;
            var suffix = 0;
            while (suffix < Count - prefix && suffix < next.Count - prefix && this[^(suffix + 1)] == next[^(suffix + 1)]) suffix++;
            var remove = Count - prefix - suffix; var add = next.Count - prefix - suffix;
            if (remove + add <= 128)
            {
                for (var i = 0; i < remove; i++) RemoveAt(prefix);
                for (var i = 0; i < add; i++) Insert(prefix + i, next[prefix + i]);
            }
            else
            {
                Items.Clear(); foreach (var row in next) Items.Add(row);
                OnPropertyChanged(new(nameof(Count))); OnPropertyChanged(new("Item[]"));
                OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
            }
        }
    }
    private readonly RowCollection _rows = [];
    private HashSet<string> _expanded = new(StringComparer.Ordinal) { "" };
    private readonly Dictionary<int, HashSet<string>> _expandedByMode = [];
    private string? _historyTreeCommit;
    private readonly HashSet<string> _filterCollapsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
    private IReadOnlyList<NavigationEntry> _entries = [];
    private DirectoryNode? _tree;
    private string? _repository;
    private bool _filtering;
    private int _mode;
    private bool _compactDirectories = true;
    public IReadOnlyList<FileNavigationRow> Rows => _rows;

    public void Update(string? repository, IReadOnlyList<NavigationEntry> entries, int mode, bool filtering, NavigationEntry? selected, bool revealSelected = true, bool compactDirectories = true)
    {
        _compactDirectories = compactDirectories;
        if (_repository != repository) { _repository = repository; _rows.Clear(); _expanded = new(StringComparer.Ordinal) { "" }; _expandedByMode.Clear(); _collapsedGroups.Clear(); _historyTreeCommit = null; }
        _expandedByMode[_mode] = _expanded;
        if (_mode != mode) _expanded = _expandedByMode.GetValueOrDefault(mode) ?? new(StringComparer.Ordinal) { "" };
        var historyKey = entries.FirstOrDefault() is { Commit: { } commit } first ? $"{first.Kind}:{first.BaseCommit}:{commit}" : null;
        var expandHistory = mode == 2 && historyKey is not null && historyKey != _historyTreeCommit;
        if (expandHistory) { _historyTreeCommit = historyKey; _expanded.Clear(); _expanded.Add(""); }
        _entries = entries; _mode = mode; _filtering = filtering; _tree = null; _filterCollapsed.Clear();
        if (mode is 0 or 2 && entries.Count > 0)
        {
            _tree = new("", System.IO.Path.GetFileName(repository) ?? "项目", 0);
            foreach (var entry in entries)
            {
                var parts = entry.Path.Split('/'); var node = _tree;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    if (!node.Directories.TryGetValue(parts[i], out var child))
                    {
                        child = new(node.Path.Length == 0 ? parts[i] : node.Path + "/" + parts[i], parts[i], node.Depth + 1);
                        node.Directories.Add(parts[i], child);
                    }
                    node = child;
                    if (expandHistory) _expanded.Add(node.Path);
                }
                node.Files.Add(entry);
            }
        }
        if (selected is not null && revealSelected) ExpandAncestors(selected.Path);
        Render();
    }

    public FileNavigationRow? ShowFile(NavigationEntry entry)
    {
        var row = _rows.FirstOrDefault(r => r.Entry == entry);
        if (row is not null) return row;
        if (_mode is 0 or 2) { ExpandAncestors(entry.Path); Render(); }
        else if (_mode == 1 && _collapsedGroups.Remove("group:" + entry.Kind)) Render();
        return _rows.FirstOrDefault(r => r.Entry == entry);
    }

    public void ExpandAll(bool expand)
    {
        _expanded.Clear();
        if (expand)
        {
            _expanded.Add("");
            foreach (var entry in _entries) ExpandAncestors(entry.Path);
        }
        Render();
    }

    private void ExpandAncestors(string path)
    {
        _expanded.Add(""); _filterCollapsed.Remove("");
        for (var i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
        { _expanded.Add(path[..i]); _filterCollapsed.Remove(path[..i]); }
    }

    public void Toggle(FileNavigationRow row)
    {
        if (row.IsGroup) { if (!_collapsedGroups.Add(row.Key)) _collapsedGroups.Remove(row.Key); Render(); return; }
        if (!row.IsDirectory) return;
        var state = _filtering ? _filterCollapsed : _expanded;
        if (!state.Add(row.Path)) state.Remove(row.Path);
        Render();
    }

    public FileNavigationRow? Parent(FileNavigationRow row)
    {
        if (_mode is not (0 or 2) || row.Path.Length == 0) return null;
        return _rows.Where(r => r.IsDirectory && r.Path != row.Path && (r.Path.Length == 0 || row.Path.StartsWith(r.Path + "/", StringComparison.Ordinal)))
            .OrderByDescending(r => r.Path.Length).FirstOrDefault();
    }

    private void Render()
    {
        var rows = new List<FileNavigationRow>();
        if (_tree is not null) AppendDirectory(_tree, rows);
        else if (_mode == 1)
        {
            foreach (var group in _entries.GroupBy(e => e.Kind))
            {
                var name = group.Key switch { ComparisonKind.LocalChanges => "更改", ComparisonKind.Index => "已暂存", ComparisonKind.Conflict => "冲突", _ => "未暂存" };
                var key = "group:" + group.Key; var expanded = !_collapsedGroups.Contains(key);
                rows.Add(new(key, "", $"{name} · {group.Count()}", 0, IsGroup: true, IsExpanded: expanded));
                if (expanded) rows.AddRange(group.Select(e => FileRow(e, 1)));
            }
        }
        else rows.AddRange(_entries.Select(e => _mode == 3 ? new FileNavigationRow($"file:{e.Kind}:{e.Path}", e.Path, e.Path, 0, e) : FileRow(e, 0)));
        _rows.Update(rows);
    }

    private static FileNavigationRow FileRow(NavigationEntry entry, int depth)
        => new($"file:{entry.Kind}:{entry.Path}", entry.Path, entry.Name, depth, entry);

    private void AppendDirectory(DirectoryNode node, List<FileNavigationRow> rows, int? displayDepth = null)
    {
        var depth = displayDepth ?? node.Depth; var name = node.Name;
        while (_compactDirectories && _mode == 2 && node.Path.Length > 0 && node.Files.Count == 0 && node.Directories.Count == 1)
        { node = node.Directories.Values.Single(); name += "/" + node.Name; }
        var open = _filtering ? !_filterCollapsed.Contains(node.Path) : _expanded.Contains(node.Path);
        rows.Add(new("directory:" + node.Path, node.Path, name, depth, IsDirectory: true, IsExpanded: open));
        if (!open) return;
        foreach (var child in node.Directories.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Name, StringComparer.Ordinal)) AppendDirectory(child, rows, depth + 1);
        rows.AddRange(node.Files.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).Select(e => FileRow(e, depth + 1)));
    }
}

public sealed record ProjectFileRevealResult(NavigationEntry? Entry, string Message = "", bool HiddenByFilter = false);
public static class ProjectFileReveal
{
    public static ProjectFileRevealResult Resolve(string? root, FileHistoryTarget? target, IReadOnlyList<NavigationEntry> entries, string filter)
    {
        if (root is null || target is null) return new(null, "没有可定位的当前文件。");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(target.Root).TrimEnd(Path.DirectorySeparatorChar), comparison))
            return new(null, "当前文件属于其他仓库或工作树，未改变项目树选择。");
        var entry = entries.FirstOrDefault(e => e.Path == target.Path);
        if (entry is null) return new(null, "文件不在当前工作树的文件清单中，可能已删除或只存在于历史版本。");
        if (filter.Length > 0 && !entry.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return new(null, "当前筛选隐藏了此文件。请修改或清除筛选后再定位；筛选条件已保留。", true);
        return new(entry);
    }
}
