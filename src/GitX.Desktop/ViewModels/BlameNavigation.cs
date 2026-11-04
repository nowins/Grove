using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed record SourceTabInfo(string Id, string Path, string? Revision, string? Label = null, bool LocalFile = false);
public enum SourceTabCloseScope { Current, Others, All }

public sealed partial class MainViewModel
{
    private sealed class SourceTabState(NavigationEntry entry, BlameRevision? target, bool annotate)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public NavigationEntry Entry { get; set; } = entry;
        public BlameRevision? Target { get; } = target;
        public bool Annotate { get; set; } = annotate;
    }
    private readonly List<SourceTabState> _sourceTabs = [];
    private readonly BlameReader _blameReader = new();
    private SourceTabState? _activeSourceTab;
    private bool _sourceTabVisible;
    private BlameRevision? _annotationTarget;
    private int? _annotationReveal;
    private long _annotationNavigation;
    public IReadOnlyList<SourceTabInfo> SourceTabs => _sourceTabs.Select(tab => new SourceTabInfo(tab.Id, tab.Target?.Path ?? tab.Entry.Path,
        tab.Target?.Sha ?? tab.Entry.Commit, tab.Entry.Kind == ComparisonKind.WorkingFile ? null :
        tab.Entry.Kind == ComparisonKind.LocalChanges ? "本地改动" : tab.Entry.Kind == ComparisonKind.Index ? "暂存" :
        tab.Entry.Kind == ComparisonKind.Worktree ? "未暂存" : tab.Entry.Kind == ComparisonKind.CommitWorktree ? "与本地比较" :
        tab.Entry.Kind == ComparisonKind.CommitParentWorktree ? "更改前与本地比较" :
        tab.Entry.BaseCommit is { } start ? $"{start[..Math.Min(8, start.Length)]} → {tab.Entry.Commit?[..Math.Min(8, tab.Entry.Commit.Length)]}" : "差异",
        LocalFile: tab.Target is null && tab.Entry.Kind == ComparisonKind.WorkingFile)).ToArray();
    public string? ActiveSourceTabId => _sourceTabVisible ? _activeSourceTab?.Id : null;
    public event Action? SourceTabsChanged;

    public Func<SourceTextCloseRequest, Task<SourceTextCloseLease>>? ConfirmSourceTextClose { get; set; }

    private void ResetSourceTabs()
    {
        SuspendSourceTab(); _sourceTabs.Clear(); _activeSourceTab = null; _blameReader.ClearCache();
    }
    private void SuspendSourceTab()
    {
        ++_annotationNavigation; _annotationTarget = null; _annotationReveal = null; _sourceTabVisible = false;
    }
    private void SetSourceTab(SourceTabState tab, int? reveal = null)
    {
        ++_annotationNavigation; _activeSourceTab = tab; _sourceTabVisible = true;
        _annotationTarget = tab.Target; _annotationReveal = reveal; _showBlame = tab.Annotate;
        Changed(nameof(BlameLabel));
    }
    private void OpenWorkingTab(NavigationEntry entry)
    {
        var tab = _sourceTabs.FirstOrDefault(tab => tab.Target is null && tab.Entry.Kind == ComparisonKind.WorkingFile && tab.Entry.Path == entry.Path);
        if (tab is null) { tab = new(entry, null, _showBlame); _sourceTabs.Add(tab); }
        tab.Entry = entry; SetSourceTab(tab);
    }
    private void OpenDocumentTab(NavigationEntry entry)
    {
        if (entry.Kind == ComparisonKind.WorkingFile) { OpenWorkingTab(entry); return; }
        var tab = _sourceTabs.FirstOrDefault(tab => tab.Target is null && tab.Entry.Path == entry.Path && tab.Entry.Kind == entry.Kind &&
            tab.Entry.Commit == entry.Commit && tab.Entry.BaseCommit == entry.BaseCommit && tab.Entry.OldPath == entry.OldPath);
        if (tab is null) { tab = new(entry, null, false); _sourceTabs.Add(tab); }
        tab.Entry = entry; SetSourceTab(tab);
    }
    private Task ActivateSourceTabAsync(SourceTabState tab, int? reveal = null)
    {
        SetEditorContext(EditorContext(tab.Entry));
        SetSourceTab(tab, reveal);
        _selectedEntry = Entries.FirstOrDefault(entry => entry.Path == tab.Entry.Path && entry.Kind == tab.Entry.Kind && entry.Commit == tab.Entry.Commit) ?? tab.Entry;
        Changed(nameof(SelectedEntry));
        return SelectEntryAsync(_selectedEntry);
    }
    public Task SelectSourceTabAsync(string id, long version)
    {
        if (_closed || version != _documentVersion || _sourceTabs.FirstOrDefault(tab => tab.Id == id) is not { } tab ||
            ActiveSourceTabId == id) return Task.CompletedTask;
        return ActivateSourceTabAsync(tab);
    }
    public Task CloseSourceTabAsync(string id, long version) => CloseSourceTabsAsync(id, version, SourceTabCloseScope.Current);
    public async Task CloseSourceTabsAsync(string id, long version, SourceTabCloseScope scope)
    {
        if (_closed || version != _documentVersion || !Enum.IsDefined(scope)) return;
        // Require the clicked tab even for All. A stale menu cannot close tabs
        // from another publication/repository after its original target vanished.
        var index = _sourceTabs.FindIndex(tab => tab.Id == id); if (index < 0) return;
        var target = _sourceTabs[index];
        bool Remove(SourceTabState tab) => scope switch
        {
            SourceTabCloseScope.Current => tab == target,
            SourceTabCloseScope.Others => tab != target,
            SourceTabCloseScope.All => true,
            _ => false
        };
        var epoch = _repositoryVersion; var tabs = _sourceTabs.Select(tab => tab.Id).ToArray();
        var ids = _sourceTabs.Where(Remove).Select(tab => tab.Id).ToArray();
        await using var lease = ConfirmSourceTextClose is { } guard ? await guard(new("tabs", ids)) : new SourceTextCloseLease(true);
        if (!lease.Allowed || _closed || epoch != _repositoryVersion || !tabs.SequenceEqual(_sourceTabs.Select(tab => tab.Id))) return;
        var removedActive = _activeSourceTab is { } active && Remove(active);
        if (_sourceTabs.RemoveAll(tab => Remove(tab)) == 0) return;
        if (!removedActive || !_sourceTabVisible)
        {
            if (removedActive) _activeSourceTab = _sourceTabs.Count > 0 ? _sourceTabs[Math.Min(index, _sourceTabs.Count - 1)] : null;
            SourceTabsChanged?.Invoke(); return;
        }
        if (_sourceTabs.Count > 0) { await ActivateSourceTabAsync(_sourceTabs[Math.Min(index, _sourceTabs.Count - 1)]); return; }
        SuspendSourceTab(); _activeSourceTab = null; _selectedEntry = null; Changed(nameof(SelectedEntry));
        ClearDocument("选择文件查看内容。"); return;
    }

    private async Task<RepositoryDocument> ReadSourceAsync(string root, NavigationEntry entry, CancellationToken token)
    {
        var target = _annotationTarget; var annotate = _showBlame; var reveal = _annotationReveal;
        if (!annotate) _ = _blameReader.PrepareAsync(root, token);
        var document = target is not null
            ? await _blameReader.ReadRevisionAsync(root, target, annotate, token)
            : annotate ? await _blameReader.ReadAsync(root, entry.Path, token)
            : await _reader.DocumentAsync(new(root, entry.Path, entry.Kind), token);
        if (target is null && annotate && entry.Kind == ComparisonKind.WorkingFile && document.SourceRevision is null && !document.IsNotice)
        {
            var working = await _reader.DocumentAsync(new(root, entry.Path, ComparisonKind.WorkingFile), token);
            if (!working.IsNotice && working.Text == document.Text) document = document with { Editing = working.Editing };
        }
        return document with { RevealLine = reveal };
    }

    public async Task PreviousAnnotationAsync(int line, long version)
    {
        if (BlameLineAt(line, version) is not { HasPrevious: true } info || _snapshot is not { } snapshot ||
            _selectedEntry is not { } entry || _document is not { } source) return;
        var owner = ++_annotationNavigation; var repository = _repositoryVersion;
        try
        {
            var target = await new BlameReader().PreviousRevisionAsync(snapshot.Root, info, source.Text, _documentRead.Token);
            if (_closed || owner != _annotationNavigation || repository != _repositoryVersion || version != _documentVersion) return;
            var tab = _sourceTabs.FirstOrDefault(tab => tab.Target?.Sha == target.Sha && tab.Target.Path == target.Path);
            if (tab is null) { tab = new(entry, target, true); _sourceTabs.Add(tab); }
            tab.Annotate = true;
            await ActivateSourceTabAsync(tab, target.Line);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && owner == _annotationNavigation && version == _documentVersion) ReportError(ex.Message); }
    }

    public Task OpenRevisionSourceAsync(string path, string sha)
    {
        if (_closed || _snapshot is null) return Task.CompletedTask;
        var tab = _sourceTabs.FirstOrDefault(tab => tab.Target?.Sha == sha && tab.Target.Path == path);
        if (tab is null)
        {
            tab = new(new(path, path, ComparisonKind.WorkingFile), new(sha, path), false);
            _sourceTabs.Add(tab);
        }
        return ActivateSourceTabAsync(tab);
    }
}
