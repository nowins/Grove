using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed record NavigationEntry(string Path, string Subtitle, ComparisonKind Kind,
    string? OldPath = null, string? Commit = null, string Status = "", string? BaseCommit = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string KindLabel => Kind switch { ComparisonKind.Index => "暂存", ComparisonKind.Worktree => "改动", ComparisonKind.Conflict => "冲突", _ => "文件" };
    public string StageLabel => Kind switch { ComparisonKind.Index => "已暂存", ComparisonKind.Worktree => "未暂存", ComparisonKind.Conflict => "冲突", _ => "" };
    public string StatusCode => Kind == ComparisonKind.Conflict ? "!" : Status switch
    { "未跟踪" or "Untracked" => "?", "新增" or "Added" => "A", "删除" or "Deleted" => "D", "重命名" or "Renamed" => "R", "修改" or "Modified" or "TypeChanged" => "M", "冲突" or "Conflicted" => "!", _ => "" };
    public bool IsAdded => StatusCode == "A";
    public bool IsDeleted => StatusCode == "D";
    public bool IsModified => StatusCode is "M" or "R";
    public bool IsConflict => StatusCode == "!";
    public string FileType => SourceLanguage.FileType(Path);
    public bool IsSource => FileType is "JV" or "C#" or "JS" or "TS" or "PY" or "PS";
}

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRepositoryReader _reader;
    private CancellationTokenSource _repositoryRead = new(), _documentRead = new(), _commitRead = new();
    private long _repositoryVersion, _documentVersion, _commitVersion, _historyVersion;
    private RepositorySnapshot? _snapshot;
    private IReadOnlyList<NavigationEntry> _allEntries = [];
    private bool _closed;
    private bool _showBlame;
    private DiffWhitespace _diffWhitespace = DiffWhitespace.Trim;
    public Task SetWhitespaceAsync(int value)
    {
        if (!Enum.IsDefined((DiffWhitespace)value) || _diffWhitespace == (DiffWhitespace)value) return Task.CompletedTask;
        _diffWhitespace = (DiffWhitespace)value;
        return SelectEntryAsync(_selectedEntry, preserve: true);
    }
    private string _repositoryPath = "", _filter = "", _status = "打开一个本地 Git 仓库", _error = "";
    private string _branch = "未打开仓库", _title = "Grove", _fileTitle = "选择文件", _comparison = "工作区 · 只读", _detail = "";
    private string _repositoryDetail = "代码、本地改动和提交历史。所有文件以只读方式打开。";
    private int _mode;
    private bool _busy, _hasMoreHistory, _historyLoading;
    private readonly NavigationCollection _entries = [];
    private readonly ObservableCollection<CommitEntry> _commits = [];
    private bool _updatingCommits;
    private NavigationEntry? _selectedEntry;
    private CommitEntry? _selectedCommit;
    private RepositoryDocument? _document;
    private TaskCompletionSource? _refreshCompletion;
    private bool _refreshAgain, _updatingEntries;
    private readonly RepositoryWriter _writer = new();
    private bool _isWriting;
    private bool _reconciling;
    public GitWriteOperation? LastGitOperation { get; private set; }
    private string _commitMessage = "";
    private DiscardRecovery? _recovery;
    private sealed record Inclusion(bool All, RepositoryDocument? Document, HashSet<ChangeLine> Lines, DiffWhitespace Whitespace = DiffWhitespace.Exact);
    private readonly Dictionary<string, Inclusion> _inclusions = new(StringComparer.Ordinal);
    public event Action? InclusionChanged;

    public MainViewModel(IRepositoryReader? reader = null) => _reader = reader ?? new RepositoryReader();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<long, RepositoryDocument>? DocumentChanged;
    public event Action<string>? RepositoryOpened;
    public event Action? NavigationChanged;
    public string? Root => _snapshot?.Root;
    public IReadOnlyList<string> RepositoryPaths => _snapshot?.Files.Select(file => file.Path).ToArray() ?? [];
    public long DocumentVersion => _documentVersion;
    public RepositoryDocument? CurrentDocument => _document;
    public bool IsChanges => _mode == 1;
    public bool CanBlame => _mode == 0 && _selectedEntry is not null;
    public string BlameLabel => _showBlame ? "关闭注解" : "注解";
    public Task ToggleBlameAsync()
    {
        if (!CanBlame) return Task.CompletedTask;
        _showBlame = !_showBlame;
        if (_activeSourceTab is not null) _activeSourceTab.Annotate = _showBlame;
        Changed(nameof(BlameLabel));
        // Hiding an annotation is a presentation change. Keep the exact source
        // snapshot and its comments instead of rereading the file from disk.
        if (!_showBlame && _document is { IsNotice: false, Diff: null } source)
        {
            Replace(ref _documentRead);
            _document = source with { Blame = null, PreserveView = true };
            var version = ++_documentVersion;
            DocumentChanged?.Invoke(version, _document);
            return Task.CompletedTask;
        }
        return SelectEntryAsync(_selectedEntry, preserve: true);
    }
    public BlameLine? BlameLineAt(int line, long version) => !_closed && _showBlame && _mode == 0 && version == _documentVersion
        ? _document?.Blame?.FirstOrDefault(item => item.Line == line) : null;
    private Task<RepositoryDocument> ReadEntryAsync(string root, NavigationEntry entry, CancellationToken token)
        => entry.Kind == ComparisonKind.WorkingFile
            ? ReadSourceAsync(root, entry, token)
            : _reader.DocumentAsync(new(root, entry.Path, entry.Kind, entry.Commit, entry.OldPath, _diffWhitespace, entry.BaseCommit), token);
    public bool HasOperation => _snapshot?.Operation is not null || _snapshot?.Changes.Any(c => c.Kind == ComparisonKind.Conflict) == true;
    public string OperationText => $"{_snapshot?.Operation?.Label ?? "未合并索引"} · {_snapshot?.Changes.Count(c => c.Kind == ComparisonKind.Conflict) ?? 0} 个冲突文件 · {_snapshot?.Operation?.Detail}";
    public bool CanContinueOperation => !IsWriting && _snapshot?.Operation is not null && !_snapshot.Changes.Any(c => c.Kind == ComparisonKind.Conflict);
    public bool CanAbortOperation => !IsWriting && _snapshot?.Operation is not null;
    public bool CanOpenConflict => !IsWriting && _selectedEntry?.Kind == ComparisonKind.Conflict;
    public Task RunOperationAsync(bool abort)
    {
        if (_snapshot is not { Operation: { } operation, IndexToken: { } expected } snapshot) return Task.CompletedTask;
        return ExecuteWriteAsync(snapshot.Root, () => new ConflictWriter().OperationAsync(snapshot.Root, expected, operation, abort));
    }
    public bool IsWriting { get => _isWriting; private set { Set(ref _isWriting, value); WriteStateChanged(); Changed(nameof(CanInteract)); } }
    public bool CanInteract => !IsWriting;
    public bool CanStageFile => !IsWriting && _mode == 1 && _selectedEntry?.Kind is ComparisonKind.Worktree or ComparisonKind.LocalChanges && _document?.WriteToken is not null;
    public bool CanUnstageFile => !IsWriting && _mode == 1 && _selectedEntry is { } selected && _document?.WriteToken is not null
        && _snapshot?.Changes.Any(change => change.Path == selected.Path && change.Kind == ComparisonKind.Index) == true;
    public bool CanCommit => !IsWriting && !HasOperation && _inclusions.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);
    public int IncludedFileCount => _inclusions.Count;
    public bool CanRestoreDiscard => !IsWriting && _recovery?.Root == Root && _recovery is not null;
    public string CommitMessage { get => _commitMessage; set { if (Set(ref _commitMessage, value)) Changed(nameof(CanCommit)); } }
    public string RecoveryDescription => _recovery is null ? "" : $"可撤销：{_recovery.Path}\n备份：{_recovery.Directory}";

    public (DocumentRequest Request, RepositoryDocument Document)? CurrentWrite()
        => Root is { } root && _selectedEntry is { } entry && _document?.WriteToken is not null
            ? (new(root, entry.Path, entry.Kind, entry.Commit, entry.OldPath), _document) : null;

    public Task WriteFileAsync(FileWriteOperation operation, DocumentRequest request, RepositoryDocument document) => ExecuteWriteAsync(request.Root, async () =>
    {
        var recovery = await _writer.FileAsync(request, document.WriteToken!, operation);
        if (recovery is not null) { _recovery = recovery; Changed(nameof(RecoveryDescription)); }
    });

    public Task StageSelectionAsync(StageSelection selection, long version)
    {
        var context = CurrentWrite();
        if (context is null || version != DocumentVersion) { ReportError("选区所属文档已变化，请重新选择。"); return Task.CompletedTask; }
        return ExecuteWriteAsync(context.Value.Request.Root, () => _writer.StageSelectionAsync(context.Value.Request, context.Value.Document, selection));
    }

    public Task RevertBlockAsync(DocumentRequest request, RepositoryDocument document, int block)
        => ExecuteWriteAsync(request.Root, async () =>
        {
            _recovery = await _writer.RevertBlockAsync(request, document, block);
            Changed(nameof(RecoveryDescription));
        }, "回退变更块");

    public async Task CommitAsync()
    {
        if (Root is not { } root || !CanCommit) return;
        var message = CommitMessage;
        // Capture the operation before the first await. Browsing another view
        // must not replace the paths, pairing policy or selection being written.
        var changes = _snapshot!.Changes.GroupBy(change => change.Path).ToDictionary(group => group.Key,
            group => group.Select(change => change.OldPath).FirstOrDefault(path => path is not null), StringComparer.Ordinal);
        var selected = _inclusions.Select(pair => (
            Request: new DocumentRequest(root, pair.Key, ComparisonKind.LocalChanges,
                OldPath: changes.GetValueOrDefault(pair.Key), Whitespace: pair.Value.Whitespace),
            Inclusion: pair.Value with { Lines = pair.Value.Lines.ToHashSet() })).ToArray();
        string? notice = null;
        GitWriteOperation? owner = null;
        await ExecuteWriteAsync(root, async () =>
        {
            owner = LastGitOperation;
            var files = new List<CommitFileSelection>();
            foreach (var (request, inclusion) in selected)
            {
                var document = await _reader.DocumentAsync(request);
                if (!inclusion.All && document.CommentContext?.Snapshot != inclusion.Document?.CommentContext?.Snapshot)
                    throw new IOException($"{request.Path} 的代码已变化，请重新选择提交范围。");
                files.Add(new(request, document, inclusion.All ? null : inclusion.Lines));
            }
            var result = await new CommitSelectionWriter().CommitAsync(root, files, message);
            notice = result.Notice;
            LastOperationOutput = result.Output; Changed(nameof(LastOperationOutput));
            _inclusions.Clear(); NotifyInclusion();
            if (CommitMessage == message) CommitMessage = "";
            if (result.Warning is { } warning) throw new IOException($"提交 {result.Sha[..8]} 已完成。{warning} 请勿重复提交。");
        }, "提交所选改动");
        if (notice is not null && !_closed && root == Root && !HasError && ReferenceEquals(owner, LastGitOperation))
            Status = "提交已完成 · " + notice;
    }

    public bool? InclusionState(string path) => !_inclusions.TryGetValue(path, out var item) ? false : item.All ? true : null;
    public IReadOnlyList<ChangeLine> CurrentIncludedLines()
    {
        if (_selectedEntry is not { } entry || _document?.Diff is not { } diff || !_inclusions.TryGetValue(entry.Path, out var inclusion)) return [];
        if (inclusion.All) return diff.Blocks.SelectMany(block => block.Rows).Select(ChangeLine.For).ToArray();
        if (inclusion.Document?.CommentContext?.Snapshot != _document.CommentContext?.Snapshot)
        { _inclusions.Remove(entry.Path); NotifyInclusion(); return []; }
        return inclusion.Lines.ToArray();
    }
    public void ToggleIncludeFile(string path)
        => SetFilesIncluded([path]);

    public void SetFilesIncluded(IEnumerable<string> paths, bool? include = null)
    {
        if (IsWriting) return;
        var available = NavigationEntries(1).Where(entry => entry.Kind == ComparisonKind.LocalChanges).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var selected = paths.Distinct(StringComparer.Ordinal).Where(available.Contains).ToArray();
        if (selected.Length == 0) return;
        var enabled = include ?? selected.Any(path => InclusionState(path) != true);
        foreach (var path in selected)
        {
            if (enabled) _inclusions[path] = new(true, null, []);
            else _inclusions.Remove(path);
        }
        NotifyInclusion();
    }
    public void ToggleIncludeAll()
        => SetFilesIncluded(Entries.Where(entry => entry.Kind == ComparisonKind.LocalChanges).Select(entry => entry.Path));
    public void IncludeLine(ChangeLine line, bool include, long version)
        => IncludeLines([line], include, version);
    public void IncludeDeletion(int block, bool include, long version)
    {
        if (version != DocumentVersion || _document?.Diff is not { } diff || block < 0 || block >= diff.Blocks.Count) return;
        IncludeLines(diff.Blocks[block].Rows.Where(row => row.NewLine is null && row.OldLine is not null).Select(ChangeLine.For).ToArray(), include, version);
    }
    private void IncludeLines(IReadOnlyList<ChangeLine> lines, bool include, long version)
    {
        if (IsWriting || version != DocumentVersion || _selectedEntry is not { Kind: ComparisonKind.LocalChanges } entry || _document?.Diff is not { } diff) return;
        var available = diff.Blocks.SelectMany(block => block.Rows).Select(ChangeLine.For).ToHashSet();
        if (lines.Count == 0 || lines.Any(line => !available.Contains(line))) return;
        var selected = CurrentIncludedLines().ToHashSet();
        if (include) selected.UnionWith(lines); else selected.ExceptWith(lines);
        if (selected.Count == 0) _inclusions.Remove(entry.Path);
        // All visible rows are still an explicit row selection. Trim/Ignore
        // may hide other edits; only the file checkbox means the whole file.
        else _inclusions[entry.Path] = new(false, _document, selected, _diffWhitespace);
        NotifyInclusion();
    }
    private void NotifyInclusion()
    { Changed(nameof(CanCommit)); Changed(nameof(IncludedFileCount)); InclusionChanged?.Invoke(); }

    public Task RestoreDiscardAsync()
    {
        if (_recovery is not { } recovery) return Task.CompletedTask;
        return ExecuteWriteAsync(recovery.Root, async () => { await _writer.RestoreAsync(recovery); _recovery = null; Changed(nameof(RecoveryDescription)); });
    }

    private async Task ExecuteWriteAsync(string root, Func<Task> action, string name = "Git 操作")
    {
        if (_closed || IsWriting || root != Root) return;
        IsWriting = true; Error = ""; Status = "Git 写入进行中 · 可继续只读浏览，任务入口可查看进度";
        var operation = new GitWriteOperation(root, name); LastGitOperation = operation; Changed(nameof(LastGitOperation));
        LastOperationOutput = "";
        string? failure = null;
        try
        {
            if (_refreshCompletion is { } refresh) await refresh.Task;
            await operation.RunAsync(action);
            Error = "";
        }
        catch (Exception e) { failure = e.Message; }
        operation.Reconciling(); _reconciling = true;
        try { if (!_closed && root == Root) await RefreshAsync(); }
        finally { _reconciling = false; IsWriting = false; }
        if (failure is null && HasError) failure = "操作后仓库读取失败，请按 F5 重新核对：" + Error;
        operation.Complete(failure);
        if (LastOperationOutput.Length == 0) LastOperationOutput = operation.Snapshot().Output;
        Changed(nameof(LastOperationOutput));
        if (!_closed && failure is not null)
        { LastOperationOutput = failure; Changed(nameof(LastOperationOutput)); ReportError(failure.Length > 1600 ? failure[..1600] + "\n完整内容见 Git → 上次 Git 输出。" : failure); }
    }

    public Task ExecuteActionAsync(string root, Func<Task<string>> action, string name = "Git 操作") => ExecuteWriteAsync(root, async () =>
    { var result = await action(); LastOperationOutput = result; Changed(nameof(LastOperationOutput)); }, name);
    public string LastOperationOutput { get; private set; } = "";

    private void WriteStateChanged()
    {
        Changed(nameof(CanBlame));
        Changed(nameof(CanStageFile)); Changed(nameof(CanUnstageFile)); Changed(nameof(CanCommit)); Changed(nameof(CanRestoreDiscard));
        Changed(nameof(HasOperation)); Changed(nameof(OperationText)); Changed(nameof(CanContinueOperation)); Changed(nameof(CanAbortOperation)); Changed(nameof(CanOpenConflict));
    }
    public string RepositoryPath { get => _repositoryPath; set { if (Set(ref _repositoryPath, value)) Changed(nameof(RepositoryName)); } }
    public string RepositoryName => string.IsNullOrWhiteSpace(RepositoryPath) ? "打开本地仓库" : Path.GetFileName(RepositoryPath.TrimEnd(Path.DirectorySeparatorChar));
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) FilterEntries(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set { Set(ref _error, value); Changed(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string Branch { get => _branch; private set { Set(ref _branch, value); Changed(nameof(BranchContext)); } }
    public string BranchContext => $"{Branch}\nHEAD  {_snapshot?.Head ?? "尚无提交"}\n工作树  {Root}\n搜索、切换、比较或合并分支";
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string FileTitle { get => _fileTitle; private set => Set(ref _fileTitle, value); }
    public string Comparison { get => _comparison; private set => Set(ref _comparison, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string RepositoryDetail { get => _repositoryDetail; private set => Set(ref _repositoryDetail, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public bool HasMoreHistory { get => _hasMoreHistory; private set { if (Set(ref _hasMoreHistory, value)) HistoryPagingChanged(); } }
    public int Mode => _mode;
    public bool IsHistory => _mode == 2;
    public string NavigationTitle => _mode switch { 0 => "项目文件", 1 => "提交", _ => HistoryFilesTitle };
    public IReadOnlyList<NavigationEntry> Entries
    {
        get => _entries;
        private set
        {
            var selected = _selectedEntry; var alreadyUpdating = _updatingEntries; _updatingEntries = true;
            try
            {
                var prefix = 0;
                while (prefix < _entries.Count && prefix < value.Count && _entries[prefix] == value[prefix]) prefix++;
                var suffix = 0;
                while (suffix < _entries.Count - prefix && suffix < value.Count - prefix && _entries[^(suffix + 1)] == value[^(suffix + 1)]) suffix++;
                var remove = _entries.Count - prefix - suffix; var add = value.Count - prefix - suffix;
                if (remove + add > 128) _entries.ReplaceAll(value);
                else
                {
                    for (var i = 0; i < remove; i++) _entries.RemoveAt(prefix);
                    for (var i = 0; i < add; i++) _entries.Insert(prefix + i, value[prefix + i]);
                }
                // Collection Reset also resets Avalonia selection even when the
                // source property is equal. Explicitly restore the binding.
                if (remove + add > 128 || selected != _selectedEntry) Changed(nameof(SelectedEntry));
            }
            finally { _updatingEntries = alreadyUpdating; }
            Changed(nameof(EmptyNavigation));
            if (!alreadyUpdating) NavigationChanged?.Invoke();
        }
    }
    private sealed class NavigationCollection : ObservableCollection<NavigationEntry>
    {
        public void ReplaceAll(IReadOnlyList<NavigationEntry> values)
        {
            Items.Clear(); foreach (var value in values) Items.Add(value);
            OnPropertyChanged(new(nameof(Count))); OnPropertyChanged(new("Item[]"));
            OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
        }
    }
    public bool EmptyNavigation => Entries.Count == 0;
    public IReadOnlyList<CommitEntry> Commits
    {
        get => _commits;
        private set
        {
            _updatingCommits = true;
            try
            {
                for (var i = 0; i < value.Count; i++)
                    if (i >= _commits.Count) _commits.Add(value[i]);
                    else if (_commits[i] != value[i]) _commits[i] = value[i];
                while (_commits.Count > value.Count) _commits.RemoveAt(_commits.Count - 1);
            }
            finally { _updatingCommits = false; }
            Changed(nameof(HasHistory));
            HistoryPagingChanged();
        }
    }
    public NavigationEntry? SelectedEntry { get => _selectedEntry; set
        {
            if (_updatingEntries) return;
            if (_mode == 0 && value?.Kind == ComparisonKind.WorkingFile)
            {
                if (Equals(_selectedEntry, value) && _sourceTabVisible && _activeSourceTab?.Target is null) return;
                OpenWorkingTab(value); Set(ref _selectedEntry, value); _ = SelectEntryAsync(value); return;
            }
            if (Equals(_selectedEntry, value)) return;
            if (value is not null) OpenDocumentTab(value);
            if (Set(ref _selectedEntry, value)) _ = SelectEntryAsync(value);
        }
    }
    public CommitEntry? SelectedCommit { get => _selectedCommit; set { if (!_updatingCommits && Set(ref _selectedCommit, value)) { Changed(nameof(HistoryCommitDetail)); _ = SelectCommitAsync(value); } } }

    public async Task OpenAsync(string path)
    {
        if (IsWriting) { ReportError("Git 操作进行中，请完成后再切换仓库。"); return; }
        if (_closed || string.IsNullOrWhiteSpace(path)) return;
        ResetSourceTabs();
        SetHistoryEntries([]);
        var previousSelection = _selectedEntry;
        var previousRoot = _snapshot?.Root;
        var version = ++_repositoryVersion; ++_historyVersion;
        Replace(ref _historyRead);
        Replace(ref _repositoryRead);
        var token = _repositoryRead.Token;
        Replace(ref _commitRead);
        ++_commitVersion;
        _snapshot = null;
        _historyLoading = false; _historyLoadFailed = false; HistoryPagingChanged();
        _allEntries = []; Entries = []; Commits = []; HasMoreHistory = false;
        _selectedEntry = null; _selectedCommit = null;
        Changed(nameof(SelectedEntry)); Changed(nameof(SelectedCommit));
        Changed(nameof(HistoryCommitDetail));
        RepositoryPath = path;
        Error = ""; Busy = true; Status = "正在读取仓库…";
        Branch = "读取中…"; RepositoryDetail = "";
        ClearDocument("正在读取仓库…");
        try
        {
            var snapshot = await _reader.OpenAsync(path, token);
            if (_closed || version != _repositoryVersion) return;
            _snapshot = snapshot;
            UpdateHistoryReferences(previousRoot != snapshot.Root);
            WriteStateChanged();
            RepositoryOpened?.Invoke(snapshot.Root);
            if (previousRoot != snapshot.Root) { _inclusions.Clear(); NotifyInclusion(); Filter = ""; _showBlame = false; Changed(nameof(BlameLabel)); }
            RepositoryPath = snapshot.Root;
            Branch = snapshot.Branch;
            Title = $"Grove — {Path.GetFileName(snapshot.Root)}";
            RepositoryDetail = $"{snapshot.Root}\n\n分支  {snapshot.Branch}\nHEAD  {snapshot.Head?[..12] ?? "尚无提交"}\n\n{snapshot.Files.Count:N0} 个文件\n{snapshot.Changes.Count:N0} 个改动项";
            Status = snapshot.FilesTruncated ? "仅显示前 20,000 个文件" : $"已读取 {snapshot.Files.Count:N0} 个文件 · 自动刷新 · F5 手动刷新";
            BuildEntries();
            ClearDocument(_mode == 2 ? "选择提交查看改动。" : "选择文件查看内容。");
            if (previousRoot == snapshot.Root && previousSelection is not null && _mode != 2)
                SelectedEntry = Entries.FirstOrDefault(e => e.Path == previousSelection.Path && e.Kind == previousSelection.Kind);
            await LoadHistoryAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!_closed && version == _repositoryVersion)
            {
                ReportError(e.Message); Branch = "未打开仓库"; Title = "Grove";
                ClearDocument("无法打开仓库。请检查上方路径后重新打开。");
            }
        }
        finally { if (!_closed && version == _repositoryVersion) Busy = false; }
    }

    public Task RefreshAsync()
    {
        if (_closed) return Task.CompletedTask;
        _blameReader.ClearCache();
        if (IsWriting && !_reconciling) { _refreshAgain = true; return Task.CompletedTask; }
        if (_snapshot is null) return OpenAsync(RepositoryPath);
        _refreshAgain = true;
        if (_refreshCompletion is not null) return _refreshCompletion.Task;
        var completion = _refreshCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RefreshLoopAsync(completion);
        return completion.Task;
    }

    private async Task RefreshLoopAsync(TaskCompletionSource completion)
    {
        var owner = _repositoryVersion;
        try
        {
            do
            {
                _refreshAgain = false;
                var snapshot = _snapshot;
                var repositoryVersion = _repositoryVersion;
                if (_closed || snapshot is null) return;
                var updated = await _reader.OpenAsync(snapshot.Root, _repositoryRead.Token);
                if (_closed || IsWriting && !_reconciling || repositoryVersion != _repositoryVersion) return;
                var selected = _selectedEntry;
                _snapshot = updated;
                WriteStateChanged();
                Branch = updated.Branch;
                RepositoryDetail = $"{updated.Root}\n\n分支  {updated.Branch}\nHEAD  {updated.Head?[..12] ?? "尚无提交"}\n\n{updated.Files.Count:N0} 个文件\n{updated.Changes.Count:N0} 个改动项";
                _updatingEntries = true;
                try { BuildEntries(); } finally { _updatingEntries = false; }
                NavigationChanged?.Invoke();
                // Refresh the active tab, including commit-to-worktree comparisons.
                // Preserve its identity even if a navigation filter hides its row.
                if (selected is { } entry)
                {
                    var documentVersion = _documentVersion;
                    var document = await ReadEntryAsync(updated.Root, entry, _repositoryRead.Token);
                    if (_closed || repositoryVersion != _repositoryVersion) return;
                    if (documentVersion == _documentVersion && entry == _selectedEntry)
                    {
                        var changed = !SameDocument(_document, document);
                        _document = document;
                        if (changed)
                        {
                            FileTitle = document.Path; Comparison = document.Comparison; Detail = document.Detail;
                            DocumentChanged?.Invoke(++_documentVersion, document with { PreserveView = true });
                        }
                        WriteStateChanged();
                    }
                }
                if (updated.Head != snapshot.Head || updated.HistoryToken != snapshot.HistoryToken)
                {
                    var generation = ++_historyVersion;
                    UpdateHistoryReferences(false);
                    await ReadHistoryPageAsync(updated, replace: true);
                    if (_closed || repositoryVersion != _repositoryVersion || generation != _historyVersion) return;
                }
                if (_historyCompareOld is not null && _historyCompareWorktree)
                    await CompareHistoryAsync(_historyCompareOld, null, preserve: true);
                Status = $"已刷新 {DateTime.Now:HH:mm:ss} · {updated.Changes.Count} 个改动项 · F5 手动刷新";
            } while (_refreshAgain && !_closed);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed && owner == _repositoryVersion) ReportError(e.Message); }
        finally { _refreshCompletion = null; completion.SetResult(); }
    }

    private static bool SameDocument(RepositoryDocument? a, RepositoryDocument b) => a is not null && a.Path == b.Path && a.Text == b.Text &&
        a.Comparison == b.Comparison && a.SourceRevision == b.SourceRevision && a.CommentContext == b.CommentContext && a.Diff?.Old == b.Diff?.Old && a.Diff?.New == b.Diff?.New && (a.Blame ?? []).SequenceEqual(b.Blame ?? []);

    public void SwitchView(int mode)
    {
        if (_closed || mode is < 0 or > 2) return;
        SetEditorContext(mode);
    }

    public Task LoadHistoryAsync(bool automatic = false)
    {
        var snapshot = _snapshot;
        if (_closed || snapshot is null || _historyLoading || automatic && !CanAutoLoadHistory) return Task.CompletedTask;
        return ReadHistoryPageAsync(snapshot, replace: _historyLoadFailed && _historyRetryReplace);
    }

    private async Task ReadHistoryPageAsync(RepositorySnapshot snapshot, bool replace)
    {
        var version = _repositoryVersion;
        var generation = _historyVersion;
        var retrying = _historyLoadFailed && Error == _historyPageError;
        if (retrying) Error = "";
        _historyLoading = true; _historyLoadFailed = false; HistoryPagingChanged();
        try
        {
            var page = await _reader.QueryHistoryAsync(snapshot.Root, HistoryHeads(snapshot), _historyQuery, replace ? 0 : Commits.Count, _historyRead.Token);
            if (_closed || version != _repositoryVersion || generation != _historyVersion) return;
            if (page.HasMore && page.Commits.Count == 0) throw new IOException("历史分页未返回提交，请重试或刷新。");
            if (replace)
            {
                Commits = page.Commits;
                if (_selectedCommit is { } oldCommit)
                { _selectedCommit = Commits.FirstOrDefault(c => c.Sha == oldCommit.Sha); Changed(nameof(SelectedCommit)); Changed(nameof(HistoryCommitDetail));
                  if (_selectedCommit is null) { Replace(ref _commitRead); ++_commitVersion; SetHistoryEntries([]); } }
            }
            else
            {
                foreach (var commit in page.Commits) _commits.Add(commit);
                Changed(nameof(HasHistory));
            }
            HasMoreHistory = page.HasMore;
            if (retrying && Error.Length == 0 && Status == "操作未完成 · 修正问题后可重试") Status = $"已读取 {Commits.Count:N0} 条提交";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed && version == _repositoryVersion && generation == _historyVersion) { _historyLoadFailed = true; _historyRetryReplace = replace; _historyPageError = e.Message; ReportError(e.Message); } }
        finally { if (version == _repositoryVersion && generation == _historyVersion) { _historyLoading = false; HistoryPagingChanged(); } }
    }

    private async Task SelectCommitAsync(CommitEntry? commit)
    {
        if (_closed) return;
        var snapshot = _snapshot;
        var repositoryVersion = _repositoryVersion;
        var version = ++_commitVersion;
        Replace(ref _commitRead);
        var token = _commitRead.Token;
        if (_closed || snapshot is null) return;
        ClearHistoryComparison();
        SetHistoryEntries([]);
        if (commit is null) return;
        try
        {
            var changes = await _reader.CommitChangesAsync(snapshot.Root, commit.Sha, token);
            if (_closed || version != _commitVersion || repositoryVersion != _repositoryVersion) return;
            SetHistoryEntries(changes.Select(c => new NavigationEntry(c.Path, c.Status, c.Kind, c.OldPath, commit.Sha, c.Status)).ToArray());
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed && version == _commitVersion) ReportError(e.Message); }
    }

    private async Task SelectEntryAsync(NavigationEntry? entry, bool preserve = false, CodeSearchMatch? location = null)
    {
        if (_closed) return;
        var snapshot = _snapshot;
        var repositoryVersion = _repositoryVersion;
        var version = ++_documentVersion;
        _document = null; WriteStateChanged();
        Replace(ref _documentRead);
        var token = _documentRead.Token;
        if (entry is null) { ClearDocument("选择文件查看内容。"); return; }
        if (_closed || snapshot is null) return;
        Error = ""; FileTitle = entry.Path; Comparison = "正在读取…"; Detail = "";
        if (!preserve) DocumentChanged?.Invoke(version, new(entry.Path, "正在读取…", "plaintext", Comparison, "", IsNotice: true));
        try
        {
            var document = await ReadEntryAsync(snapshot.Root, entry, token);
            if (_closed || version != _documentVersion || repositoryVersion != _repositoryVersion) return;
            if (location?.Fingerprint is { } fingerprint)
            {
                var currentFingerprint = await Task.Run(() => RepositoryReader.SearchFingerprint(document.Text), token);
                if (_closed || version != _documentVersion || repositoryVersion != _repositoryVersion) return;
                if (document.IsNotice || currentFingerprint != fingerprint) throw new IOException("搜索后文件内容已变化，请重新搜索后定位。");
            }
            if (location is not null) document = document with { RevealLine = location.Line, RevealColumn = location.Column, RevealLength = location.Length };
            FileTitle = document.Path;
            Comparison = document.Comparison; Detail = document.Detail;
            _document = document = document with { PreserveView = preserve };
            WriteStateChanged();
            DocumentChanged?.Invoke(version, document);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!_closed && version == _documentVersion)
            {
                ReportError(e.Message); Comparison = "读取失败";
                DocumentChanged?.Invoke(version, new(entry.Path, e.Message, "plaintext", Comparison, "", IsNotice: true));
            }
        }
    }

    private void BuildEntries()
    {
        _allEntries = NavigationEntries(_mode);
        FilterEntries();
        if (_snapshot is not null)
        {
            var present = NavigationEntries(1).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
            var removed = _inclusions.Keys.Where(path => !present.Contains(path)).ToArray();
            foreach (var path in removed) _inclusions.Remove(path);
            if (removed.Length > 0) NotifyInclusion();
        }
    }

    private void FilterEntries() => Entries = string.IsNullOrWhiteSpace(Filter) ? _allEntries :
        _allEntries.Where(e => e.Path.Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToArray();

    private void ClearDocument(string text)
    {
        Replace(ref _documentRead);
        _document = null;
        WriteStateChanged();
        FileTitle = "选择文件"; Comparison = "只读"; Detail = "";
        DocumentChanged?.Invoke(++_documentVersion, new("", text, "plaintext", "只读", "", IsNotice: true));
    }

    public void ReportError(string message) { Error = message; Status = "操作未完成 · 修正问题后可重试"; }
    private static void Replace(ref CancellationTokenSource source) { source.Cancel(); source.Dispose(); source = new(); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _blameReader.ClearCache();
        foreach (var source in new[] { _repositoryRead, _documentRead, _commitRead, _historyRead, _noteRead }) { source.Cancel(); source.Dispose(); }
        (_reader as IDisposable)?.Dispose();
        DocumentChanged = null;
        RepositoryOpened = null;
        InclusionChanged = null;
    }
}
