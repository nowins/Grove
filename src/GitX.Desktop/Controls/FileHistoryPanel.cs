using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop.Controls;

// IDEA's file-history tool window: revision list and inline diff share one dock.
internal sealed class FileHistoryPanel : Border, IDisposable
{
    private readonly Window _owner;
    private Window CurrentOwner => TopLevel.GetTopLevel(this) as Window ?? _owner;
    private readonly FileHistoryViewModel _model;
    private readonly ListBox _commits = new() { Focusable = true, FocusAdorner = null, Background = Brushes.Transparent, BorderThickness = new(0), AutoScrollToSelectedItem = false };
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Button _refresh = new() { Classes = { "toolbar", "icon-button" }, Content = IconContent.Icon("RefreshIcon") };
    private readonly Button _retry = new() { Classes = { "toolbar" }, Content = "重试", IsVisible = false };
    private readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = UiTypography.SecondarySize, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _body;
    private EditorServer? _server;
    private NativeWebView? _view;
    private bool _started, _closed, _ready, _syncSelection, _pagingQueued;
    private long _publication, _publishedVersion;
    private double _lastWidth;
    private string? _dockView;
    private long _dockViewVersion;
    public FileHistoryTarget Target => _model.Target;
    public event Action<int>? ToolRequested;
    public event Action<bool, bool>? SearchRequested;
    public event Action? HideRequested;
    public event Action? ResetLayoutRequested;
    public event Action? CloseRequested;
    public event Action<FileHistoryTarget?>? RevealRequested;

    public FileHistoryPanel(Window owner, FileHistoryTarget target)
    {
        _owner = owner; _model = new(target);
        Background = Brush("SurfaceBrush");
        var layout = new Grid { RowDefinitions = new("28,*") };
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new(8, 0, 4, 0) };
        header.Children.Add(new TextBlock { Text = "文件历史：", VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = target.Path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var scope = target.UntilRevision is { } until ? "截至 " + until[..Math.Min(8, until.Length)] : "全部分支";
        ToolTip.SetTip(title, target.Root + " / " + target.Path + "\n" + scope + " · 按路径查询，不跟随重命名");
        Grid.SetColumn(title, 1); header.Children.Add(title);
        var close = new Button { Classes = { "toolbar", "icon-button" }, Content = IconContent.Icon("CloseIcon") };
        AutomationProperties.SetName(close, "关闭文件历史"); ToolTip.SetTip(close, "关闭文件历史 · Esc");
        close.Click += (_, _) => CloseRequested?.Invoke();
        var reveal = new Button { Classes = { "toolbar", "icon-button" }, Content = IconContent.Icon("RevealIcon") };
        AutomationProperties.SetName(reveal, "在项目树定位历史文件"); ToolTip.SetTip(reveal, "在当前工作树定位同路径文件 · Ctrl+Shift+E；保留历史版本");
        reveal.Click += (_, _) => RevealCurrentFile();
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal }; headerActions.Children.Add(reveal); headerActions.Children.Add(close);
        Grid.SetColumn(headerActions, 2); header.Children.Add(headerActions); layout.Children.Add(header);
        _body = new Grid { ColumnDefinitions = new("360,4,*") };
        _body.ColumnDefinitions[0].MinWidth = 200; _body.ColumnDefinitions[0].MaxWidth = 520;
        var revisions = new Grid { RowDefinitions = new("28,*,Auto") };
        var tools = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(8, 0, 4, 0) };
        tools.Children.Add(new TextBlock { Text = scope, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
        AutomationProperties.SetName(_refresh, "刷新文件历史"); ToolTip.SetTip(_refresh, "刷新文件历史 · F5");
        Grid.SetColumn(_refresh, 1); tools.Children.Add(_refresh); revisions.Children.Add(tools);
        _commits.Name = "FileHistoryCommits"; AutomationProperties.SetName(_commits, "文件历史提交列表");
        _commits.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = {
            new Setter(ListBoxItem.HeightProperty, 24d), new Setter(ListBoxItem.MinHeightProperty, 24d),
            new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 0)), new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _commits.ItemTemplate = new FuncDataTemplate<CommitEntry>((commit, _) =>
        {
            if (commit is null) return new Border();
            var row = new Grid { ColumnDefinitions = new("64,104,*"), Height = 24 };
            row.Children.Add(new TextBlock { Text = commit.Author, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var date = new TextBlock { Text = commit.When.LocalDateTime.ToString("yyyy/MM/dd HH:mm"), FontSize = UiTypography.SecondarySize, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(date, 1); row.Children.Add(date);
            var subject = new TextBlock { Text = commit.Subject, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(subject, 2); row.Children.Add(subject);
            ToolTip.SetTip(row, $"{commit.Sha}\n{commit.Author} · {commit.DateLabel}\n\n{commit.FullMessage}");
            AutomationProperties.SetName(row, commit.Detail + " " + commit.Subject); return row;
        });
        _commits.ItemsSource = _model.Commits;
        _commits.SelectionChanged += (_, _) => { if (!_syncSelection) _model.SelectedCommit = _commits.SelectedItem as CommitEntry; };
        _commits.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => QueueHistoryPage());
        _commits.LayoutUpdated += (_, _) => QueueHistoryPage();
        Grid.SetRow(_commits, 1); revisions.Children.Add(_commits);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(8, 3) };
        footer.Children.Add(_status); Grid.SetColumn(_retry, 1); footer.Children.Add(_retry);
        AutomationProperties.SetName(_retry, "重试加载文件历史");
        Grid.SetRow(footer, 2); revisions.Children.Add(footer);
        _body.Children.Add(revisions);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(splitter, 1); _body.Children.Add(splitter);
        Grid.SetColumn(_host, 2); _body.Children.Add(_host); Grid.SetRow(_body, 1); layout.Children.Add(_body); Child = layout;
        _refresh.Click += async (_, _) => await _model.RefreshAsync(); _retry.Click += async (_, _) => await _model.RetryAsync();
        _model.PropertyChanged += ModelChanged;
        _model.DocumentChanged += PublishDocument;
        AttachedToVisualTree += (_, _) => { if (!_started) { _started = true; Dispatcher.UIThread.Post(async () => await InitializeAsync()); } };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty || Bounds.Width <= 0 || Math.Abs(Bounds.Width - _lastWidth) < 1) return;
            _lastWidth = Bounds.Width;
            var narrow = Bounds.Width < 760;
            _body.RowDefinitions = narrow ? new("*,4,2*") : new("*");
            _body.ColumnDefinitions[0].MinWidth = narrow ? 0 : 200;
            _body.ColumnDefinitions[0].MaxWidth = narrow ? double.PositiveInfinity : 520;
            _body.ColumnDefinitions[0].Width = narrow ? new(1, GridUnitType.Star) : new(Math.Clamp(Bounds.Width * .30, 200, 390));
            _body.ColumnDefinitions[1].Width = new(narrow ? 0 : 4);
            _body.ColumnDefinitions[2].Width = narrow ? new(0) : new(1, GridUnitType.Star);
            Grid.SetColumn(revisions, 0); Grid.SetColumnSpan(revisions, narrow ? 3 : 1);
            Grid.SetColumn(splitter, narrow ? 0 : 1); Grid.SetRow(splitter, narrow ? 1 : 0); Grid.SetColumnSpan(splitter, narrow ? 3 : 1);
            Grid.SetColumn(_host, narrow ? 0 : 2); Grid.SetRow(_host, narrow ? 2 : 0); Grid.SetColumnSpan(_host, narrow ? 3 : 1);
            splitter.ResizeDirection = narrow ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        };
    }
    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
    public void FocusCommits() => NativeKeyboardFocus.TryFocusControl(CurrentOwner, _commits);
    public async Task CaptureDockViewAsync()
    {
        if (_closed || !_ready || _view is null) return;
        var version = _model.DocumentVersion;
        try
        {
            var view = await _view.InvokeJsonQueryAsync("window.gitx.captureDockView()").WaitAsync(TimeSpan.FromSeconds(2));
            if (!_closed && version == _model.DocumentVersion && !string.IsNullOrWhiteSpace(view) && view != "null")
            { _dockView = view; _dockViewVersion = version; }
        }
        catch (Exception) { /* A closing or not-yet-created native child has no view to preserve. */ }
    }
    public async Task RestoreDockViewAsync()
    {
        if (!_closed && _ready && _view is not null && _dockView is { } view && _dockViewVersion == _model.DocumentVersion)
        {
            try { await _view.InvokeCommandAsync("window.gitx.restoreDockView(" + view + ")").WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* The ready/loaded handshake retries after a native reparent. */ }
        }
    }
    public void FocusDiff()
    { if (!_closed && _ready) { _view?.Focus(); _ = _view?.InvokeCommandAsync("window.gitx.focus()"); } }
    private void RevealCurrentFile() => RevealRequested?.Invoke(_model.Document is { IsNotice: false, Path.Length: > 0 } document ? new(Target.Root, document.Path) : null);
    public bool HandleKey(KeyEventArgs e)
    {
        if (!CurrentOwner.IsActive || !IsKeyboardFocusWithin) return false;
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.E) RevealCurrentFile();
        else if (e.Key == Key.Escape) CloseRequested?.Invoke();
        else if (e.Key == Key.F5) _ = _model.RefreshAsync();
        else if (e.Key == Key.F7 && _ready)
        { _view?.Focus(); _ = _view?.InvokeCommandAsync($"window.gitx.navigate({(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1)})"); }
        else return false;
        e.Handled = true; return true;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed) return;
        if (e.PropertyName == nameof(FileHistoryViewModel.SelectedCommit))
        {
            _syncSelection = true; _commits.SelectedItem = _model.SelectedCommit; _syncSelection = false;
            if (_model.SelectedCommit is { } selected) _commits.ScrollIntoView(selected);
        }
        _refresh.IsEnabled = !_model.Loading; _retry.IsVisible = _model.LoadFailed; _retry.IsEnabled = !_model.Loading;
        _status.Text = _model.Status; ToolTip.SetTip(_status, _model.LoadError ?? _model.Status);
    }
    private void QueueHistoryPage()
    {
        if (_closed || _pagingQueued || !_model.CanAutoLoad || !_commits.IsEffectivelyVisible || TopLevel.GetTopLevel(_commits) is null) return;
        _pagingQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _pagingQueued = false;
            if (_closed || !_model.CanAutoLoad || !_commits.IsEffectivelyVisible || TopLevel.GetTopLevel(_commits) is null) return;
            var scroll = _commits.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is null || scroll.Viewport.Height <= 0) return;
            if (scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y <= Math.Max(8 * 24, scroll.Viewport.Height / 2))
                await _model.LoadMoreAsync(automatic: true);
        }, DispatcherPriority.Background);
    }
    private async Task InitializeAsync()
    {
        if (_closed) return;
        try
        {
            _server = new EditorServer(); _view = new NativeWebView();
            DesktopWebView.Configure(_view);
            _view.NavigationStarted += (_, e) => { if (_server is null || !_server.Owns(e.Request)) e.Cancel = true; };
            _view.NewWindowRequested += (_, e) => e.Handled = true;
            _view.WebMessageReceived += (_, e) => Dispatcher.UIThread.Post(async () => await EditorMessage.DispatchAsync(e.Body,
                () => new(_closed, _publishedVersion, _model.DocumentVersion, Target.Root), HandleMessageAsync, ShowError));
            _view.Source = _server.Address; _host.Content = _view;
            await _model.RefreshAsync();
        }
        catch (Exception ex) { if (!_closed) ShowError(ex.Message); }
    }
    private async Task HandleMessageAsync(EditorMessage message)
    {
        switch (message.Type)
        {
            case "ready": _ready = true; if (_publishedVersion > 0) await LoadPublishedAsync(); break;
            case "loaded": await RestoreDockViewAsync(); break;
            case "view": ToolRequested?.Invoke(message.Payload.GetProperty("index").GetInt32()); break;
            case "code-search": SearchRequested?.Invoke(message.Payload.GetProperty("filesOnly").GetBoolean(), message.Payload.TryGetProperty("quick", out var quick) && quick.GetBoolean()); break;
            case "whitespace": await _model.SetWhitespaceAsync((DiffWhitespace)message.Payload.GetProperty("value").GetInt32()); break;
            case "refresh": await _model.RefreshAsync(); break;
            case "focus-navigation": FocusCommits(); break;
            case "close-document": CloseRequested?.Invoke(); break;
            case "project-reveal": RevealCurrentFile(); break;
            case "hide-tool": HideRequested?.Invoke(); break;
            case "reset-layout": ResetLayoutRequested?.Invoke(); break;
            case "focus-out": if (_view is not null) NativeKeyboardFocus.LeaveEmbedded(CurrentOwner, _view, message.Payload.GetProperty("direction").GetInt32()); break;
            case "error": ShowError(message.Payload.GetProperty("message").GetString()!); break;
        }
    }
    private async void PublishDocument(long version, RepositoryDocument document)
    {
        var publication = ++_publication;
        try
        {
            var payload = await Task.Run(() => EditorServer.Serialize(version, document, new(Target.Path, false, "", FileHistory: true)));
            if (_closed || publication != _publication || version != _model.DocumentVersion || _server is null) return;
            _server.Publish(payload); _publishedVersion = version;
            if (_ready) await LoadPublishedAsync();
        }
        catch (Exception ex) { if (!_closed && publication == _publication) ShowError(ex.Message); }
    }
    private async Task LoadPublishedAsync()
    { if (!_closed && _view is not null) await _view.InvokeCommandAsync($"window.gitx.load({_publishedVersion})"); }
    private void ShowError(string text) { if (_closed) return; _status.Text = text; ToolTip.SetTip(_status, text); }
    public void Dispose()
    {
        if (_closed) return;
        _closed = true; ++_publication;
        _model.DocumentChanged -= PublishDocument; _model.PropertyChanged -= ModelChanged; _model.Dispose();
        _host.Content = null; _view = null; _server?.Dispose();
    }
}
