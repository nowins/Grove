using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow : Window
{
    private static string LocalDataDirectory => Environment.GetEnvironmentVariable("GITX_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitX");
    private readonly MainViewModel _model = new();
    private EditorServer? _server;
    private NativeWebView? _webView;
    private RepositoryWatcher? _watcher;
    private string? _watchedRoot;
    private readonly TextBox _fallback = new() { Classes = { "code" }, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
    private bool _ready, _closed;
    private long _publishedVersion;
    private long _publicationSequence;
    private Window? _mergeWindow;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialRepository)
    {
        InitializeComponent();
        InitializeTitlebar();
        WindowsWindowCommands.Attach(this);
        DataContext = _model;
        FilesPane.DataContext = _model;
        InitializeNavigation();
        InitializeHistory();
        InitializeGitTasks();
        InitializeDocking();
        foreach (var list in new[] { FileList, _projectList })
            list.SizeChanged += (_, _) => list.Classes.Set("compact-file-list", list.Bounds.Width < 420);
        InitializeShell();
        _blameViewReady = LoadBlameViewAsync();
        _model.ConfirmSourceTextClose = ConfirmTextCloseAsync;
        _model.CaptureReadingPosition = CaptureReadingPositionAsync;
        _model.CaptureContinuousPosition = CaptureContinuousPositionAsync;
        _model.DocumentChanged += OnDocumentChanged;
        _model.SourceTabsChanged += OnSourceTabsChanged;
        _model.InclusionChanged += RefreshInclusion;
        _model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsWriting)) RefreshInclusion(); };
        InitializeDraftRecovery();
        _model.RepositoryOpened += WatchRepository;
        _model.RepositoryOpened += _ => CloseFileHistory(false);
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, DoubleShiftDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, DoubleShiftUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => _doubleShift.Reset(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TextInputEvent, (_, _) => _doubleShift.Reset(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => _doubleShift.Reset(), RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _doubleShift.Reset();
        SizeChanged += (_, _) =>
        {
            UpdateNavigationLayout();
        };
        Opened += async (_, _) =>
        {
            if (_dock is not null) await _dock.LoadAsync();
            UpdateNavigationLayout();
            InitializeEditor();
            if (!string.IsNullOrWhiteSpace(initialRepository)) await _model.OpenAsync(initialRepository);
        };
        Closing += async (_, e) =>
        {
            if (_commentBusy) { e.Cancel = true; _model.ReportError("评论操作尚未完成，请等待完成后关闭。"); return; }
            if (_mergeWindow is not null) { e.Cancel = true; _mergeWindow.Activate(); return; }
            if (_model.IsWriting) { e.Cancel = true; ShowGitTask(); return; }
            if (_commentDraft is not null) { e.Cancel = true; await ConfirmCommentCloseAsync(); return; }
            if (_updateBusy) { e.Cancel = true; await CancelUpdateForCloseAsync(); if (!_closed) Close(); return; }
            if (!_dockCloseReady && _dock is not null)
            { e.Cancel = true; await _dock.FlushAsync(); await _recentFileWrite; _dockCloseReady = true; Close(); return; }
            if (!_textCloseReady) { e.Cancel = true; await ConfirmTextWindowCloseAsync(); return; }
            _dock?.PrepareOwnerClose();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _draftTimer.Stop();
            _dock?.Dispose();
            CloseFileHistory(false);
            _blameRead?.Cancel(); _blameRead?.Dispose();
            _watcher?.Dispose();
            _model.Dispose();
            EditorHost.Content = null;
            _webView = null;
            _server?.Dispose();
        };
    }

    private async void WatchRepository(string root)
    {
        if (_watchedRoot == root) return;
        _watchedRoot = root;
        _watcher?.Dispose(); _watcher = null;
        try
        {
            var watcher = await Task.Run(() => new RepositoryWatcher(root, changes => Dispatcher.UIThread.Post(() =>
            {
                if (!_closed && _model.Root == root) _ = _model.RefreshAsync(changes);
            })));
            if (_closed || _watchedRoot != root) watcher.Dispose(); else _watcher = watcher;
        }
        catch (Exception e) { if (!_closed && _watchedRoot == root) _model.ReportError($"自动刷新暂不可用，可按 F5 刷新：{e.Message}"); }
    }

    private void InitializeEditor()
    {
        try
        {
            var profile = Environment.GetEnvironmentVariable("GITX_DATA_DIR") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitX", "WebView2");
            Directory.CreateDirectory(profile);
            var probe = Path.Combine(profile, $"write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            _server = new EditorServer();
            _webView = new NativeWebView();
            DesktopWebView.Configure(_webView, profile);
            _webView.NavigationStarted += (_, e) => { if (!_server.Owns(e.Request)) e.Cancel = true; };
            _webView.NewWindowRequested += (_, e) => e.Handled = true;
            _webView.WebMessageReceived += (_, e) => Dispatcher.UIThread.Post(() => _ = HandleWebMessage(e.Body));
            _webView.Source = _server.Address;
            EditorHost.Content = _webView;
        }
        catch (Exception e)
        {
            FallbackFileToolbar.IsVisible = true;
            var panel = new DockPanel();
            var warning = new TextBlock
            {
                Text = $"编辑器初始化失败，当前使用纯文本预览：{e.Message}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(12), Foreground = Avalonia.Media.Brushes.Salmon
            };
            DockPanel.SetDock(warning, Dock.Top);
            panel.Children.Add(warning);
            panel.Children.Add(_fallback);
            EditorHost.Content = panel;
            _server?.Dispose(); _server = null;
        }
    }

    private async void OnDocumentChanged(long version, RepositoryDocument document)
    {
        _loadedEditorVersion = -1;
        EditorPosition.Text = "";
        EditorFormat.Text = "";
        await PublishEditorDocumentAsync(version, document);
    }

    private RepositoryDocument? _publishedDocument;

    private async void OnSourceTabsChanged()
    {
        if ((_model.CurrentDocument ?? (_publishedVersion == _model.DocumentVersion ? _publishedDocument : null)) is { } document)
            await PublishEditorDocumentAsync(_model.DocumentVersion, document, tabsOnly: true);
    }

    private async Task PublishEditorDocumentAsync(long version, RepositoryDocument document, bool tabsOnly = false)
    {
        _textEditable.IntersectWith(_model.SourceTabs.Where(tab => tab.LocalFile).Select(tab => tab.Id));
        tabsOnly &= _loadedEditorVersion == version && _publishedVersion == version && _publishedDocument == document;
        if (!tabsOnly) _blameRead?.Cancel();
        var sequence = ++_publicationSequence;
        if (_server is null) { _fallback.Text = document.Text; return; }
        try
        {
            await _blameViewReady;
            if (_closed || sequence != _publicationSequence) return;
            var toolbar = new EditorToolbarState(_model.FileTitle, _model.CanBlame, _model.BlameLabel, _model.CurrentIncludedLines(),
                _model.SelectedEntry is { } selected ? _model.InclusionState(selected.Path) : false, _blameView, _model.SourceTabs, _model.ActiveSourceTabId, _model.Mode == 0, ReadingNavigation: true,
                ContinuousDiff: _model.ContinuousDiff?.Manifest, CanContinuousDiff: _model.CanOpenContinuousDiff, WorkspaceActions: _model.Root is not null, EmptyWorkspace: _model.EmptyWorkspace,
                DiffFileNavigation: _model.CanNavigateDiffFiles);
            var markdown = !document.IsDiff && !document.IsNotice && document.Language == "markdown" && _model.Root is { } markdownRoot
                ? new MarkdownResourceContext(version, markdownRoot, document.Path, document.SourceRevision) : null;
            var server = _server; var repository = _model.Root;
            var continuous = _model.ContinuousDiff;
            var publication = await Task.Run(() => server.PreparePublication(version, document, toolbar, markdown, repository, continuous));
            if (_closed || sequence != _publicationSequence) return;
            server.Publish(publication);
            _publishedDocument = document;
            _publishedVersion = version;
            if (_ready) await InvokeEditorAsync(tabsOnly
                ? "window.gitx.setSourceTabs(" + JsonSerializer.Serialize(new { toolbar.SourceTabs, toolbar.ActiveSourceTabId, toolbar.SourceMode }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + ")"
                : $"window.gitx.load({version})");
            if (tabsOnly) return;
            await RefreshCommentsAsync();
        }
        catch (Exception e) { if (!_closed && sequence == _publicationSequence) _model.ReportError($"编辑器文档加载失败：{e.Message}"); }
    }

    private Task HandleWebMessage(string? body) => EditorMessage.DispatchAsync(body,
        () => new(_closed, _publishedVersion, _model.DocumentVersion, _model.Root), DispatchEditorMessageAsync, _model.ReportError);

    private async Task DispatchEditorMessageAsync(EditorMessage message)
    {
        var root = message.Payload;
        var version = message.Version ?? -1;
        switch (message.Type)
        {
            case "ready":
                _ready = true;
                if (_publishedVersion > 0) await InvokeEditorAsync($"window.gitx.load({_publishedVersion})");
                break;
            case "loaded":
                RefreshInclusion();
                if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Number && models.TryGetInt32(out var count) && count > 0)
                    await FocusRequestedSourceAsync(version);
                await ApplyPendingDraftAsync();
                break;
            case "refresh": await RefreshCurrentAsync(); break;
            case "comment": await HandleCommentAsync(root); break;
            case "open": await PickRepositoryAsync(); break;
            case "view": SwitchView(root.GetProperty("index").GetInt32(), focusNavigation: true); break;
            case "recent-files": await OpenRecentFilesAsync(); break;
            case "code-search": await OpenCodeSearchAsync(root.GetProperty("filesOnly").GetBoolean(), root.TryGetProperty("quick", out var quick) && quick.GetBoolean()); break;
            case "source-history": await OpenSourceHistoryAsync(version); break;
            case "markdown-open":
                if (_publishedDocument is not { IsDiff: false, IsNotice: false, Language: "markdown" }) break;
                var href = root.GetProperty("href").GetString()!;
                if (MarkdownResources.ExternalLink(href) is { } external)
                    await Launcher.LaunchUriAsync(external);
                else await _model.OpenMarkdownLinkAsync(href, version);
                break;
            case "project-reveal": RevealProjectFile(); break;
            case "history-back": if (_model.IsHistory) NavigateHistoryBack(); break;
            case "reading-history": await NavigateReadingAsync(root.GetProperty("direction").GetInt32(), legacyLog: true); break;
            case "continuous-diff":
                if (root.GetProperty("action").GetString() == "open") await _model.OpenContinuousDiffAsync(root.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0);
                else if (root.GetProperty("action").GetString() == "page") _model.PageContinuousDiff(root.GetProperty("id").GetString()!, root.GetProperty("offset").GetInt32(), version);
                else if (await _model.OpenContinuousDiffFileAsync(root.GetProperty("id").GetString()!, root.GetProperty("index").GetInt32(),
                    root.GetProperty("side").GetInt32(), root.GetProperty("line").GetInt32(), version)) RequestSourceFocus();
                break;
            case "diff-file":
                var diffDirection = root.GetProperty("direction").GetInt32();
                var diffInput = _inputEpoch;
                if (await _model.NavigateDiffFileAsync(diffDirection, root.GetProperty("side").GetInt32(), version) && diffInput == _inputEpoch && IsActive) RequestSourceFocus();
                await InvokeEditorAsync($"window.gitx.diffNavigationDone({version},{diffDirection})");
                break;
            case "blame": await _model.ToggleBlameAsync(); break;
            case "blame-action": await HandleBlameActionAsync(root.GetProperty("action").GetString()!, root.GetProperty("line").GetInt32(), version); break;
            case "blame-view": await HandleBlameViewAsync(root.GetProperty("field").GetString()!, root.GetProperty("value").GetBoolean(), version); break;
            case "source-save": await SaveTextAsync(root.GetProperty("id").GetString()); break;
            case "source-edit-state":
                ScheduleDraftCheckpoint();
                var textId = root.GetProperty("id").GetString()!;
                if (_model.SourceTabs.Any(t => t.Id == textId && t.LocalFile))
                { if (root.GetProperty("editable").GetBoolean()) _textEditable.Add(textId); else _textEditable.Remove(textId); RefreshSaveMenuState(); }
                break;
            case "source-tab":
                var id = root.GetProperty("id").GetString()!;
                switch (root.GetProperty("action").GetString())
                {
                    case "close": await _model.CloseSourceTabAsync(id, version); break;
                    case "close-others": await _model.CloseSourceTabsAsync(id, version, SourceTabCloseScope.Others); break;
                    case "close-all": await _model.CloseSourceTabsAsync(id, version, SourceTabCloseScope.All); break;
                    case "select": await _model.SelectSourceTabAsync(id, version); break;
                    case "reveal": await OpenTabDesktopLocationAsync(id, DesktopLocationAction.Reveal); break;
                    case "terminal": await OpenTabDesktopLocationAsync(id, DesktopLocationAction.Terminal); break;
                }
                break;
            case "stage-selection":
                var selection = new StageSelection(root.GetProperty("block").ValueKind == JsonValueKind.Null ? null : root.GetProperty("block").GetInt32(),
                    root.GetProperty("side").GetInt32(), root.GetProperty("startLine").GetInt32(), root.GetProperty("endLine").GetInt32());
                await _model.StageSelectionAsync(selection, version);
                break;
            case "revert-block":
                await RevertBlockAsync(root.GetProperty("block").GetInt32(), version);
                break;
            case "include-line":
                _model.IncludeLine(new(root.GetProperty("side").GetInt32(), root.GetProperty("line").GetInt32()),
                    root.GetProperty("include").GetBoolean(), version);
                break;
            case "caret":
                _model.RecordReadingPosition(new(version, root.GetProperty("side").GetInt32(), root.GetProperty("line").GetInt32(), root.GetProperty("column").GetInt32()),
                    root.TryGetProperty("navigation", out var readingNavigation) && readingNavigation.GetBoolean());
                EditorPosition.Text = $"{(root.GetProperty("side").GetInt32() == 0 ? "旧侧" : "当前侧")}  {root.GetProperty("line").GetInt32()}:{root.GetProperty("column").GetInt32()}";
                break;
            case "editor-status":
                EditorFormat.Text = root.GetProperty("text").GetString();
                ToolTip.SetTip(EditorFormat, EditorFormat.Text);
                break;
            case "include-file":
                if (_model.SelectedEntry is { } includeEntry)
                    _model.ToggleIncludeFile(includeEntry.Path);
                break;
            case "include-deletion":
                _model.IncludeDeletion(root.GetProperty("block").GetInt32(), root.GetProperty("include").GetBoolean(), version);
                break;
            case "whitespace": await _model.SetWhitespaceAsync(root.GetProperty("value").GetInt32()); break;
            case "close-document":
                if (_model.ActiveSourceTabId is { } activeTab) await _model.CloseSourceTabAsync(activeTab, version);
                else _model.SelectedEntry = null;
                break;
            case "focus-navigation": _model.CancelDiffNavigation(); FocusFileList(); break;
            case "hide-tool": FocusEditor(); break;
            case "reset-layout": if (_dock is not null) await _dock.ResetAsync(); break;
            case "focus-out":
                _model.CancelDiffNavigation();
                if (_webView is not null) NativeKeyboardFocus.LeaveEmbedded(this, _webView, root.GetProperty("direction").GetInt32());
                break;
            case "error": _model.ReportError(root.GetProperty("message").GetString()!); break;
        }
    }

    private async Task InvokeEditorAsync(string script)
    {
        if (_closed || _webView is null) return;
        try { await _webView.InvokeCommandAsync(script); }
        catch (Exception e) { if (!_closed) _model.ReportError($"编辑器通信失败：{e.Message}"); }
    }

    private Task NavigateDiffAsync(int direction)
    {
        if (_closed || !_ready || _webView is null) return Task.CompletedTask;
        OpenHistoryDiff();
        // Monaco.focus() only selects a DOM target. Transfer native keyboard focus
        // first, so the next shortcut reaches WebView2 instead of the file list.
        _webView.Focus();
        return InvokeEditorAsync($"window.gitx.navigate({direction})");
    }

    private void FocusFileList()
    {
        // Ignore a late bridge request if the user has already left this editor.
        if (!IsActive || (_webView?.IsFocused != true && !NativeKeyboardFocus.HasEmbeddedFocus(this))) return;
        if (_model.IsHistory && _bottomOpen && !_fileHistoryActive) { ReturnToHistory(false); return; }
        var source = _model.Mode == 0;
        OpenTool(source ? 0 : 1, false, false);
        var list = source ? _projectList : FileList;
        if (list.SelectedIndex >= 0) list.ScrollIntoView(list.SelectedIndex);
        var target = list.ContainerFromIndex(list.SelectedIndex) ?? list;
        NativeKeyboardFocus.TryFocusControl(this, target);
    }

    private async Task PickRepositoryAsync()
    {
        if (!CanChangeRepository()) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "选择本地 Git 仓库", AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) await OpenRepositoryAsync(path);
        }
        catch (Exception e) { _model.ReportError(e.Message); }
    }

    private void SwitchView(int index, bool focusNavigation = false)
    {
        if (index is < 0 or > 2) return;
        OpenTool(index, false, focusNavigation);
    }

    private void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape && _dock?.CancelDrag() == true) { e.Handled = true; return; }
        if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.Shift) { e.Handled = true; if (_dock is not null) _ = _dock.ResetAsync(); return; }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.Shift)
        { e.Handled = true; _dock?.HideFocused(); FocusEditor(); return; }
        if (e.Key == Key.E && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
        { e.Handled = true; _ = OpenRecentFilesAsync(); return; }
        if (e.Key == Key.S && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) && _webView?.IsKeyboardFocusWithin == true)
        { e.Handled = true; _ = SaveTextAsync(); return; }
        if (_fileHistoryPanel?.HandleKey(e) == true) return;
        if (HistoryFileShortcut(e)) return;
        if (ReadingKeyDown(e)) return;
        if (!OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.M) { e.Handled = true; MainMenuClicked(MainMenuButton, new RoutedEventArgs()); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.E) { e.Handled = true; RevealProjectFile(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F && _projectPanel.IsKeyboardFocusWithin) { e.Handled = true; _projectFilterVisible = true; UpdateProjectFilter(); NativeKeyboardFocus.TryFocusControl(this, _projectFilter); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key is Key.F or Key.N)
        { e.Handled = true; _ = OpenCodeSearchAsync(e.Key == Key.N); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F12)
        { e.Handled = true; _ = OpenSourceSymbolsAsync(); }
        else if (_model.IsHistory && e.KeyModifiers == KeyModifiers.Control && e.Key == Key.D) { e.Handled = true; OpenHistoryDiff(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F4 && _model.ActiveSourceTabId is { } sourceTab)
        { e.Handled = true; _ = _model.CloseSourceTabAsync(sourceTab, _model.DocumentVersion); }
        else if (e.Key == Key.F5) { e.Handled = true; _ = RefreshCurrentAsync(); }
        else if (e.Key == Key.F7) { e.Handled = true; _ = NavigateDiffAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.R) { e.Handled = true; _ = InvokeEditorAsync("window.gitx.revertBlock()"); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.O) { e.Handled = true; _ = PickRepositoryAsync(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.D1 or Key.D2 or Key.D3)
        { e.Handled = true; SwitchView((int)e.Key - (int)Key.D1, focusNavigation: true); }
    }

    private async void OpenClicked(object? sender, RoutedEventArgs e) => await PickRepositoryAsync();
    private async void ShowConflictsClicked(object? sender, RoutedEventArgs e)
    {
        SwitchView(1); await OpenConflictListAsync();
    }
    private async void MergeClicked(object? sender, RoutedEventArgs e) => await OpenMergeAsync();
    private async void FileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is FileNavigationRow { IsDirectory: true } row) { ToggleDirectory(row); e.Handled = true; return; }
        if (_model.CanOpenConflict) { e.Handled = true; await OpenMergeAsync(); }
        else { e.Handled = true; Activate(); RequestSourceFocus(); }
    }
    private async Task OpenMergeAsync()
    {
        if (!_model.CanOpenConflict || _model.SelectedEntry is not { } entry) return;
        await OpenConflictListAsync(entry.Path, openSelected: true);
    }
    private async Task OpenConflictListAsync(string? path = null, bool openSelected = false)
    {
        if (_mergeWindow is not null) { _mergeWindow.Activate(); return; }
        if (_model.IsWriting || _model.Root is not { } root) return;
        _mergeWindow = new ConflictsWindow(root, path, openSelected);
        try { await _mergeWindow.ShowDialog(this); }
        finally { _mergeWindow = null; await ReturnToNavigationAfterAsync(_model.RefreshAsync); }
    }
    private async void ContinueOperationClicked(object? sender, RoutedEventArgs e) => await _model.RunOperationAsync(false);
    private async void AbortOperationClicked(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmDialog.ShowAsync(this, "中止 Git 操作", $"{_model.OperationText}\n\nGit 将回到操作开始前的状态，当前冲突处理结果可能被撤销。受影响文件会先备份到 Git 元数据目录 gitx-recovery。", "中止并保留备份"))
            await _model.RunOperationAsync(true);
    }
    private async void StageClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.CurrentWrite() is { } context) await ReturnToNavigationAfterAsync(() => _model.WriteFileAsync(FileWriteOperation.Stage, context.Request, context.Document));
    }
    private async void UnstageClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.CurrentWrite() is { } context) await ReturnToNavigationAfterAsync(() => _model.WriteFileAsync(FileWriteOperation.Unstage, context.Request, context.Document));
    }
    private async void DiscardClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.CurrentWrite() is not { } context) return;
        await DiscardFileAsync(context);
    }
    private async Task DiscardFileAsync((DocumentRequest Request, RepositoryDocument Document) context)
    {
        var localChanges = context.Request.Kind == ComparisonKind.LocalChanges;
        var dialog = new Window { Title = localChanges ? "回退文件改动" : "丢弃未暂存改动", Width = 460, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var discard = new Button { Content = "丢弃并保留备份" };
        cancel.Click += (_, _) => dialog.Close(false);
        discard.Click += (_, _) => dialog.Close(true);
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(discard);
        var content = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 16 };
        var source = localChanges ? "HEAD 版本，索引内容保留" : "索引版本";
        content.Children.Add(new TextBlock { Text = $"回退 {context.Request.Path} 的改动？\n\n工作区将恢复到{source}；未跟踪文件将被移除。内容会备份到仓库 Git 元数据目录，可通过“撤销丢弃”恢复。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(buttons); dialog.Content = content; DialogChrome.Apply(dialog);
        dialog.Opened += (_, _) => cancel.Focus();
        if (await dialog.ShowDialog<bool>(this)) await _model.WriteFileAsync(FileWriteOperation.Discard, context.Request, context.Document);
    }
    private async Task RevertBlockAsync(int block, long version)
    {
        if (_model.IsWriting || version != _publishedVersion || version != _model.DocumentVersion || _model.CurrentWrite() is not { } context
            || context.Request.Kind is not (ComparisonKind.Worktree or ComparisonKind.LocalChanges) || context.Document.Diff is not { Old.Exists: true, New.Exists: true } diff
            || block < 0 || block >= diff.Blocks.Count) return;
        // Diff actions never save or discard a source tab's in-memory draft.
        // Refuse a dirty/busy sibling instead of silently changing its disk baseline.
        try
        {
            await using var lease = await GuardDiskWriteAsync(context.Request.Root, [context.Request.Path]);
            if (!lease.Allowed) return;
            if (_closed || _model.IsWriting || version != _publishedVersion || version != _model.DocumentVersion) return;
            await _model.RevertBlockAsync(context.Request, context.Document, block);
        }
        catch (Exception error) { _model.ReportError(error.Message); }
    }
    private async void CommitClicked(object? sender, RoutedEventArgs e) => await _model.CommitAsync();
    private async void CommitAndPushClicked(object? sender, RoutedEventArgs e)
    { await _model.CommitAsync(); if (!_model.HasError && _model.IncludedFileCount == 0) await PushAfterCommitAsync(); }
    private void IncludeFileClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FileNavigationRow row) return;
        if (row.IsGroup) ToggleVisibleChanges(); else _model.ToggleIncludeFile(row.Path);
    }
    private void ShowFileFilterClicked(object? sender, RoutedEventArgs e)
    { _commitFilterVisible = !string.IsNullOrEmpty(FilterInput.Text) || !_commitFilterVisible; UpdateNavigationLayout(); if (_commitFilterVisible) NativeKeyboardFocus.TryFocusControl(this, FilterInput); }
    private void RefreshInclusion()
    {
        var files = _navigation.Rows.Where(row => row.Entry?.Kind == ComparisonKind.LocalChanges).ToArray();
        foreach (var row in files) row.SetIncluded(_model.InclusionState(row.Path), _model.CanInteract);
        var states = _model.NavigationEntries(1).Where(entry => entry.Kind == ComparisonKind.LocalChanges).Select(entry => _model.InclusionState(entry.Path)).ToArray();
        foreach (var group in _navigation.Rows.Where(row => row.IsGroup && row.CanInclude))
            group.SetIncluded(states.Length > 0 && states.All(state => state == true) ? true : states.All(state => state == false) ? false : null, _model.CanInteract);
        if (_ready && _model.CurrentDocument?.CanInclude == true && _publishedVersion == _model.DocumentVersion)
        {
            var state = JsonSerializer.Serialize(new { version = _publishedVersion, lines = _model.CurrentIncludedLines(), busy = _model.IsWriting,
                included = _model.SelectedEntry is { } selected ? _model.InclusionState(selected.Path) : false },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            _ = InvokeEditorAsync($"window.gitx.setInclusion({state})");
        }
    }
    private async void RestoreDiscardClicked(object? sender, RoutedEventArgs e)
    {
        var recovery = _model.CurrentRecovery;
        var epoch = _model.RepositoryGeneration;
        if (recovery is null || !_model.CanRestoreDiscard) return;
        try
        {
            await using var lease = await GuardDiskWriteAsync(recovery.Root, DraftWriteGuard.Paths(recovery));
            if (!lease.Allowed) return;
            if (_closed || epoch != _model.RepositoryGeneration || !ReferenceEquals(recovery, _model.CurrentRecovery) || !_model.CanRestoreDiscard) return;
            await _model.RestoreDiscardAsync();
        }
        catch (Exception error) { _model.ReportError(error.Message); }
    }
    private async void RefreshClicked(object? sender, RoutedEventArgs e) => await RefreshCurrentAsync();
    private async void MoreHistoryClicked(object? sender, RoutedEventArgs e) => await _model.LoadHistoryAsync();
    private void BrowseClicked(object? sender, RoutedEventArgs e) => OpenTool(0, true, true);
    private void ChangesClicked(object? sender, RoutedEventArgs e) => OpenTool(1, true, true);
    private void HistoryClicked(object? sender, RoutedEventArgs e) => OpenTool(2, true, true);
    private void InfoClicked(object? sender, RoutedEventArgs e)
    { if (_model.IsHistory) HistoryFilesToggled(sender, e); }
    private async void PreviousHunkClicked(object? sender, RoutedEventArgs e) => await NavigateDiffAsync(-1);
    private async void NextHunkClicked(object? sender, RoutedEventArgs e) => await NavigateDiffAsync(1);
}
