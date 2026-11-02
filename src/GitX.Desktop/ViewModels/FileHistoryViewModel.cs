using System.Collections.ObjectModel;
using System.ComponentModel;
using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed record FileHistoryTarget(string Root, string Path, string? UntilRevision = null);

// A read-only session with its own reader and cancellation. Selecting revisions
// never changes the main window's source, history filters or navigation state.
public sealed class FileHistoryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRepositoryReader _reader;
    private readonly bool _ownsReader;
    private CancellationTokenSource _historyRead = new(), _documentRead = new();
    private long _historyGeneration, _selectionGeneration;
    private bool _closed, _loading, _hasMore, _loadFailed;
    private string? _loadError;
    private string? _heads;
    private CommitEntry? _selected;
    private DiffWhitespace _whitespace = DiffWhitespace.Trim;
    private string _status = "";

    public FileHistoryViewModel(FileHistoryTarget target, IRepositoryReader? reader = null)
    { Target = target; _reader = reader ?? new RepositoryReader(); _ownsReader = reader is null; }
    public FileHistoryTarget Target { get; }
    public ObservableCollection<CommitEntry> Commits { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<long, RepositoryDocument>? DocumentChanged;
    public long DocumentVersion { get; private set; }
    public RepositoryDocument? Document { get; private set; }
    public bool Loading => _loading;
    public bool HasMore => _hasMore;
    public bool LoadFailed => _loadFailed;
    public string? LoadError => _loadError;
    public bool CanAutoLoad => !_closed && _hasMore && !_loading && !_loadFailed;
    public string Status => _status;
    public CommitEntry? SelectedCommit
    {
        get => _selected;
        set => _ = SelectAsync(value);
    }
    public Task SelectAsync(CommitEntry? value)
    {
        if (_closed || _selected == value || value is null && _loading || value is not null && !Commits.Contains(value)) return Task.CompletedTask;
        _selected = value; Changed(nameof(SelectedCommit));
        return ReadSelectedAsync();
    }

    public async Task RefreshAsync()
    {
        if (_closed) return;
        var previous = _selected?.Sha;
        var generation = ++_historyGeneration;
        Replace(ref _historyRead); var token = _historyRead.Token;
        ++_selectionGeneration; Replace(ref _documentRead);
        _loading = true; _hasMore = false; _selected = null; _loadFailed = false; _loadError = null;
        SetStatus("正在读取文件历史…"); NotifyHistory();
        Commits.Clear(); Changed(nameof(SelectedCommit));
        PublishNotice("正在读取文件历史…");
        CommitEntry? selected = null;
        var loaded = false;
        try
        {
            var snapshot = await _reader.OpenAsync(Target.Root, token);
            if (!Current(generation)) return;
            var heads = Target.UntilRevision ?? snapshot.HistoryHeads ?? snapshot.Head;
            var page = await _reader.QueryHistoryAsync(Target.Root, heads, new(Path: Target.Path), 0, token);
            if (!Current(generation)) return;
            CheckPage(page);
            _heads = heads;
            foreach (var commit in page.Commits) Commits.Add(commit);
            _hasMore = page.HasMore; _loading = false; NotifyHistory();
            SetLoadedStatus();
            selected = Commits.FirstOrDefault(c => c.Sha == previous) ?? Commits.FirstOrDefault();
            loaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Current(generation)) { FailPage(ex); PublishNotice("历史读取失败：" + ex.Message); }
        }
        finally { if (Current(generation)) { _loading = false; NotifyHistory(); } }
        if (!Current(generation) || !loaded) return;
        if (selected is null) PublishNotice("此路径暂无 Git 历史。未提交的新文件没有历史记录。");
        else await SelectAsync(selected);
    }

    public Task RetryAsync() => _hasMore ? LoadMoreAsync() : RefreshAsync();

    public async Task LoadMoreAsync(bool automatic = false)
    {
        if (_closed || _loading || !_hasMore || automatic && !CanAutoLoad) return;
        var generation = _historyGeneration;
        var token = _historyRead.Token;
        _loading = true; _loadFailed = false; _loadError = null;
        SetStatus("正在读取提交…"); NotifyHistory();
        try
        {
            var page = await _reader.QueryHistoryAsync(Target.Root, _heads, new(Path: Target.Path), Commits.Count, token);
            if (!Current(generation)) return;
            CheckPage(page);
            foreach (var commit in page.Commits) Commits.Add(commit);
            _hasMore = page.HasMore;
            SetLoadedStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) FailPage(ex); }
        finally { if (Current(generation)) { _loading = false; NotifyHistory(); } }
    }

    public Task SetWhitespaceAsync(DiffWhitespace value)
    {
        if (_closed || !Enum.IsDefined(value) || _whitespace == value) return Task.CompletedTask;
        _whitespace = value; return ReadSelectedAsync();
    }
    private async Task ReadSelectedAsync()
    {
        var generation = ++_selectionGeneration;
        Replace(ref _documentRead); var token = _documentRead.Token;
        var selected = _selected;
        PublishNotice(selected is null ? "选择提交查看此文件的差异。" : "正在读取所选提交的文件差异…");
        if (selected is null) return;
        try
        {
            var document = await _reader.DocumentAsync(new(Target.Root, Target.Path, ComparisonKind.Commit,
                selected.Sha, Whitespace: _whitespace), token);
            if (_closed || generation != _selectionGeneration || selected != _selected) return;
            // Revision comparisons do not expose local note or Git-write actions.
            Publish(document with { CommentContext = null, CanStage = false, CanInclude = false, WriteToken = null });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && generation == _selectionGeneration) PublishNotice("差异读取失败：" + ex.Message); }
    }
    private bool Current(long generation) => !_closed && generation == _historyGeneration;
    private void PublishNotice(string text) => Publish(new(Target.Path, text, "plaintext", "文件历史", "", IsNotice: true));
    private void Publish(RepositoryDocument document)
    { if (_closed) return; Document = document; DocumentChanged?.Invoke(++DocumentVersion, document); }
    private void SetStatus(string text) { _status = text; Changed(nameof(Status)); }
    private void SetLoadedStatus() => SetStatus(Commits.Count == 0 ? "此路径暂无 Git 历史" : _hasMore
        ? $"已加载 {Commits.Count:N0} 条提交 · 向下滚动继续加载" : $"已加载全部 {Commits.Count:N0} 条提交");
    private static void CheckPage(HistoryPage page)
    { if (page.HasMore && page.Commits.Count == 0) throw new IOException("历史分页未返回提交，请重试或刷新。"); }
    private void FailPage(Exception error)
    { _loadFailed = true; _loadError = error.Message; SetStatus("加载失败 · 已保留当前列表"); NotifyHistory(); }
    private void NotifyHistory()
    { Changed(nameof(Loading)); Changed(nameof(HasMore)); Changed(nameof(LoadFailed)); Changed(nameof(LoadError)); Changed(nameof(CanAutoLoad)); }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    private static void Replace(ref CancellationTokenSource source)
    { source.Cancel(); source.Dispose(); source = new(); }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; ++_historyGeneration; ++_selectionGeneration;
        _historyRead.Cancel(); _documentRead.Cancel(); _historyRead.Dispose(); _documentRead.Dispose();
        if (_ownsReader && _reader is IDisposable disposable) disposable.Dispose();
    }
}
