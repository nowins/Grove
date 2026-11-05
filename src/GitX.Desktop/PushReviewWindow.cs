using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

internal sealed class PushReviewWindow : Window
{
    private readonly string _root;
    private readonly Func<bool> _isCurrent;
    private readonly Func<Func<Task<string>>, Task> _execute;
    private readonly Func<GitWriteOperation?> _getOperation;
    private readonly Func<Window, GitWriteOperation, Task> _terminateOperation;
    private readonly Button _terminate = new() { Content = "终止本次 Git 进程…", IsVisible = false, IsEnabled = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private GitWriteOperation? _writingOperation;
    private readonly PushPreviewReader _reader = new();
    private readonly ComboBox _remote;
    private readonly TextBox _branch;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "dialog-hint" } };
    private readonly ListBox _commits = new();
    private readonly ListBox _files = new();
    private readonly Button _read = new() { Content = "刷新审阅" };
    private readonly Button _push = new() { Content = "推送", IsEnabled = false, Classes = { "primary" } };
    private readonly Button _close = new() { Content = "关闭", IsCancel = true };
    private readonly Button _next = new() { Content = "下一页", IsEnabled = false };
    private readonly Button _previous = new() { Content = "上一页", IsEnabled = false };
    private readonly CheckBox _force = new() { Content = "Force with lease：允许改写远程历史" };
    private readonly bool _ephemeral;
    private PushPreview? _preview;
    private CancellationTokenSource? _reading;
    private CancellationTokenSource? _fileReading, _pageReading;
    private int _generation, _fileGeneration, _offset;
    private bool _writing, _closed;

    public PushReviewWindow(string root, ActionContext context, IReadOnlyList<RemoteEntry> remotes, RemoteEntry? selected,
        string? branch, bool requireChoice, bool force, bool ephemeral, Func<bool> isCurrent, Func<Func<Task<string>>, Task> execute,
        Func<GitWriteOperation?> getOperation, Func<Window, GitWriteOperation, Task> terminateOperation)
    {
        _root = root; _isCurrent = isCurrent; _execute = execute; _ephemeral = ephemeral;
        _getOperation = getOperation; _terminateOperation = terminateOperation;
        Title = "推送前审阅"; Width = 850; Height = 620; MinWidth = 620; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _remote = new() { ItemsSource = remotes, SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        _branch = new() { Text = requireChoice ? "" : branch ?? "", PlaceholderText = "明确输入远程目标分支" };
        var choices = new StackPanel { Spacing = 6, IsVisible = requireChoice };
        if (requireChoice)
        {
            var upstream = new Button { Content = new TextBlock { Text = "使用跟踪分支：" + branch, TextWrapping = TextWrapping.Wrap } };
            var same = new Button { Content = new TextBlock { Text = "使用同名分支：" + context.Branch, TextWrapping = TextWrapping.Wrap } };
            upstream.Click += (_, _) => { if (!_writing) _branch.Text = branch; };
            same.Click += (_, _) => { if (!_writing) _branch.Text = context.Branch; };
            choices.Children.Add(upstream); choices.Children.Add(same);
        }
        _force.IsVisible = force; _force.IsChecked = force;
        var targets = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        targets.Children.Add(_remote); Grid.SetColumn(_branch, 1); targets.Children.Add(_branch);
        var lists = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        lists.Children.Add(_commits); Grid.SetColumn(_files, 1); lists.Children.Add(_files);
        var labels = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        labels.Children.Add(new TextBlock { Text = "待推提交 · 每页 100 项", Classes = { "dialog-hint" } });
        var filesLabel = new TextBlock { Text = "所选提交的文件 · 相对第一父提交，最多 1000 项", TextWrapping = TextWrapping.Wrap, Classes = { "dialog-hint" } };
        Grid.SetColumn(filesLabel, 1); labels.Children.Add(filesLabel);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _previous, _next, _terminate } };
        var body = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto,Auto,Auto"), RowSpacing = 10 };
        Control[] rows = [targets, choices, _summary, labels, lists, pages, _force, _status];
        for (var i = 0; i < rows.Length; i++) { Grid.SetRow(rows[i], i); body.Children.Add(rows[i]); }
        Content = DialogChrome.Layout(body, DialogChrome.Buttons(_read, _close, _push));
        DialogChrome.Apply(this);
        _remote.SelectionChanged += (_, _) => Invalidate();
        _branch.TextChanged += (_, _) => Invalidate();
        _force.IsCheckedChanged += (_, _) => UpdatePush();
        _read.Click += async (_, _) => await ReadAsync();
        _push.Click += async (_, _) => await PushAsync();
        _close.Click += (_, _) => Close();
        _terminate.Click += async (_, _) =>
        {
            if (_writingOperation is { } operation) await _terminateOperation(this, operation);
        };
        _timer.Tick += (_, _) => _terminate.IsEnabled = _writing && _writingOperation?.Snapshot() is { Running: true, TerminationRequested: false, ProcessId: not null };
        _timer.Start();
        _next.Click += async (_, _) => await PageAsync(_offset + 100);
        _previous.Click += async (_, _) => await PageAsync(Math.Max(0, _offset - 100));
        _commits.SelectionChanged += async (_, _) => await FilesAsync();
        Closing += (_, e) => { if (_writing) e.Cancel = true; };
        Closed += (_, _) => { _closed = true; _timer.Stop(); Invalidate(); };
        Opened += async (_, _) => { if (!requireChoice && selected is not null && !string.IsNullOrWhiteSpace(branch)) await ReadAsync(); };
        _status.Text = requireChoice ? "本地分支与跟踪分支异名，请明确选择目标，再刷新审阅。" : "选择远程和目标分支，刷新后审阅。";
    }

    private void Invalidate()
    {
        _generation++; _fileGeneration++; _reading?.Cancel(); _fileReading?.Cancel(); _pageReading?.Cancel();
        _preview = null; _commits.ItemsSource = null; _files.ItemsSource = null;
        _push.IsEnabled = _next.IsEnabled = _previous.IsEnabled = false;
        if (!_writing) _read.IsEnabled = true;
        _summary.Text = "目标已变更，需要刷新审阅。";
    }

    private async Task<GitAskPassSession> AuthenticationAsync()
    {
        await Task.CompletedTask;
        return new GitAskPassSession(Environment.ProcessPath!, prompt => Dispatcher.UIThread.InvokeAsync(() =>
            InputDialog.ShowAsync(this, "Git 凭据", prompt + "\n凭据仅传给当前 Git 进程。", secret: !prompt.StartsWith("Username", StringComparison.OrdinalIgnoreCase))), _ephemeral);
    }

    private async Task ReadAsync()
    {
        if (_writing || !_isCurrent() || _remote.SelectedItem is not RemoteEntry remote) return;
        Invalidate(); var generation = _generation; var branch = _branch.Text ?? "";
        using var cancellation = new CancellationTokenSource(); _reading = cancellation; _read.IsEnabled = false;
        _status.Text = "正在读取实际推送端的远程引用；不会自动 Fetch。";
        try
        {
            await using var auth = await AuthenticationAsync();
            var context = await RepositoryActions.ReadAsync(_root, cancellation.Token);
            var preview = await _reader.ReadAsync(context, remote, branch, auth.Authentication, cancellation.Token);
            if (!Current(generation)) return;
            _preview = preview;
            _summary.Text = $"{context.Branch} → {remote.Name}/{branch}\n{RemoteWriter.RedactUrl(preview.PushUrl)}\nHEAD {context.Head?[..8]} · 远程 {(preview.RemoteHead.Length == 0 ? "不存在" : preview.RemoteHead[..8])} · {preview.ReadAt.ToLocalTime():HH:mm:ss}";
            _status.Text = preview.Relation switch
            {
                PushRelation.NewBranch => $"将创建远程分支，共 {preview.CommitCount} 个可达提交；不会改绑本地 upstream。",
                PushRelation.Ahead => $"待推 {preview.CommitCount} 个提交。未提交的工作区改动不会推送。",
                PushRelation.Synchronized => "已同步，无需推送。",
                PushRelation.Behind => "远程领先，本地没有待推提交。普通推送不可用。",
                PushRelation.Diverged => "本地与远程已分叉。请先 Fetch 后合并或变基；改写历史仅在高级流程开放。",
                _ => "远程提交不在本地，待推数量未知。请关闭后在远程菜单 Fetch，再重新审阅；独立 push URL 可能需要先显式获取该仓库。"
            };
            UpdatePush();
            if (preview.Relation != PushRelation.Unknown) await PageAsync(0);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) { _preview = null; _push.IsEnabled = false; _status.Text = RemoteWriter.RedactUrl(ex.Message); } }
        finally { if (ReferenceEquals(_reading, cancellation)) { _reading = null; if (!_closed) _read.IsEnabled = true; } }
    }

    private bool Current(int generation) => !_closed && generation == _generation && _isCurrent();
    private void UpdatePush()
    {
        _push.IsEnabled = !_writing && _preview is { } p && (p.CanPush || _force.IsChecked == true && p.Relation is PushRelation.Behind or PushRelation.Diverged);
        _push.Content = _force.IsChecked == true ? "Force with lease · 改写远程" : _preview?.Relation == PushRelation.NewBranch ? "创建远程分支并推送" : $"推送 {_preview?.CommitCount ?? 0} 个提交";
    }
    private async Task PageAsync(int offset)
    {
        if (_preview is not { } preview || _writing) return;
        var generation = _generation; _offset = offset;
        _pageReading?.Cancel();
        using var cancellation = new CancellationTokenSource(); _pageReading = cancellation;
        try
        {
            var rows = await _reader.CommitsAsync(preview, offset, cancellation.Token);
            if (!Current(generation) || _offset != offset || cancellation.IsCancellationRequested) return;
            _commits.ItemsSource = rows; _commits.SelectedIndex = rows.Count > 0 ? 0 : -1;
            _previous.IsEnabled = offset > 0; _next.IsEnabled = offset + rows.Count < preview.CommitCount;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(generation)) { _preview = null; _push.IsEnabled = false; _status.Text = RemoteWriter.RedactUrl(ex.Message); } }
        finally { if (ReferenceEquals(_pageReading, cancellation)) _pageReading = null; }
    }
    private async Task FilesAsync()
    {
        _fileReading?.Cancel(); var generation = ++_fileGeneration; _files.ItemsSource = null;
        if (_preview is not { } preview || _commits.SelectedItem is not PushCommit commit) return;
        using var cancellation = new CancellationTokenSource(); _fileReading = cancellation;
        try
        {
            var rows = await _reader.FilesAsync(preview, commit, cancellation.Token);
            if (!_closed && _isCurrent() && generation == _fileGeneration && ReferenceEquals(_preview, preview)) _files.ItemsSource = rows;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && generation == _fileGeneration) _status.Text = RemoteWriter.RedactUrl(ex.Message); }
        finally { if (ReferenceEquals(_fileReading, cancellation)) _fileReading = null; }
    }
    private async Task PushAsync()
    {
        if (_writing || !_isCurrent() || !_push.IsEnabled || _preview is not { } preview) return;
        _pageReading?.Cancel(); _fileReading?.Cancel();
        _writing = true; _push.IsEnabled = _remote.IsEnabled = _branch.IsEnabled = _read.IsEnabled = _force.IsEnabled = false;
        _terminate.IsVisible = true;
        _status.Text = "正在推送；完成前不能关闭窗口。远端读取最多等待 30 秒；Git 写入可从此窗口确认终止。";
        string? output = null; Exception? failure = null;
        try
        {
            await using var auth = await AuthenticationAsync();
            await _execute(async () =>
            {
                _writingOperation = _getOperation();
                try { return output = await _reader.ExecuteAsync(preview, _force.IsChecked == true, auth.Authentication); }
                catch (Exception ex) { failure = ex; throw; }
            });
            _status.Text = failure is not null ? RemoteWriter.RedactUrl(failure.Message) : output is null ? "未开始推送，请刷新审阅。" : "推送已完成。\n" + output;
        }
        catch (Exception ex) { _status.Text = RemoteWriter.RedactUrl(ex.Message); }
        finally
        {
            _writing = false; _writingOperation = null; _terminate.IsVisible = false; _preview = null; _push.IsEnabled = false;
            _remote.IsEnabled = _branch.IsEnabled = _read.IsEnabled = _force.IsEnabled = true;
        }
    }
}
