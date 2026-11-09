using Avalonia.Controls;
using Avalonia.Interactivity;
using GitX.Desktop.Controls;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private ToolDockWorkspace? _dock;
    private bool _dockCloseReady;
    private int _leftTool => 1; // The original file pane is now exclusively Commit.
    private bool _leftOpen => _dock?.Visible(ToolId.Commit) == true;
    private bool _bottomOpen => _dock?.Visible(ToolId.GitLog) == true || _dock?.Visible(ToolId.FileHistory) == true;
    private bool _fileHistoryActive => _dock?.Visible(ToolId.FileHistory) == true;
    private bool _commitFilterVisible;
    private bool _historyWide, _historyLeftVisible, _historyRightVisible;
    private readonly HistoryPanePreferences _historyPanePreferences = new();
    private double _commitHeight = 230, _commitAppliedHeight = 230;
    private double _gitBranchWidth = 180, _gitFilesWidth = 300, _gitAppliedBranchWidth = 180, _gitAppliedFilesWidth = 300;
    private readonly string[] _toolFilters = ["", ""];
    private readonly string?[] _toolSelections = new string?[2];
    internal Window InteractionWindow => _dock?.ActiveWindow ?? this;

    private void InitializeDocking()
    {
        InitializeProjectTool();
        InitializeWorktreeTool();
        InitializeUpdateTool();
        _dock = new(this, EditorPane, Path.Combine(LocalDataDirectory, "tool-layout.json"));
        foreach (var control in new Control[] { _projectPanel, LocalNavigation, GitLogWorkspace, FileHistoryHost }) control.DataContext = _model;
        _dock.Register(ToolId.Project, "项目", "FolderIcon", _projectHost);
        _dock.Register(ToolId.Commit, "提交", "ChangesIcon", LocalNavigation);
        _dock.Register(ToolId.GitLog, "Git · Log", "HistoryIcon", GitLogWorkspace);
        _dock.Register(ToolId.FileHistory, "文件历史", "FileIcon", FileHistoryHost);
        _dock.Register(ToolId.Output, "Git 输出", "OutputIcon", _gitOutputPanel);
        _dock.Register(ToolId.Worktrees, "工作树", "BranchIcon", _worktreesPanel);
        _dock.Register(ToolId.Updates, "更新", "RefreshIcon", _updatesPanel);
        _dock.Error += _model.ReportError;
        _dock.BeforeReparentAsync = id => id is null or ToolId.FileHistory ? _fileHistoryPanel?.CaptureDockViewAsync() ?? Task.CompletedTask : Task.CompletedTask;
        _dock.AfterReparentAsync = id => id is null or ToolId.FileHistory ? _fileHistoryPanel?.RestoreDockViewAsync() ?? Task.CompletedTask : Task.CompletedTask;
        _dock.Activated += id =>
        {
            UpdateEditorFocusButton();
            switch (id)
            {
                case ToolId.Project: if (_revisionBrowser is { } revision) revision.FocusFiles(); else NativeKeyboardFocus.TryFocusControl(this, _projectList); break;
                case ToolId.Commit: NativeKeyboardFocus.TryFocusControl(this, FileList); break;
                case ToolId.GitLog: NativeKeyboardFocus.TryFocusControl(this, CommitList); break;
                case ToolId.FileHistory: _fileHistoryPanel?.FocusCommits(); break;
                case ToolId.Output: UpdateGitOutput(); NativeKeyboardFocus.TryFocusControl(this, _gitOutputText); break;
                case ToolId.Worktrees: _ = RefreshWorktreePanelAsync(); _worktreesPanel.FocusList(); break;
                case ToolId.Updates: _updatesPanel.FocusCheck(); break;
            }
        };
        _dock.FloatingCreated += window =>
        {
            window.AddHandler(PointerPressedEvent, (_, _) => _inputEpoch++, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(KeyDownEvent, (_, _) => _inputEpoch++, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(KeyDownEvent, WindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            window.AddHandler(KeyDownEvent, DoubleShiftDown, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(KeyUpEvent, DoubleShiftUp, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            window.Deactivated += (_, _) => { _doubleShift.Reset(); _inputEpoch++; };
        };
        DockWorkspaceHost.Content = _dock;
        LocalNavigation.SizeChanged += (_, _) => UpdateNavigationLayout();
        GitLogWorkspace.SizeChanged += (_, _) => UpdateNavigationLayout();
    }

    private void UpdateNavigationLayout()
    {
        FileList.SelectionMode = SelectionMode.Multiple;
        CommitFileActions.IsVisible = CommitMessageSplitter.IsVisible = CommitMessagePane.IsVisible = true;
        FilterInput.IsVisible = _commitFilterVisible || !string.IsNullOrEmpty(FilterInput.Text);
        var max = Math.Max(100, Math.Min(LocalNavigation.Bounds.Height * .45, LocalNavigation.Bounds.Height - 180));
        var current = FilesPane.RowDefinitions[4].Height;
        if (current.IsAbsolute && Math.Abs(current.Value - _commitAppliedHeight) > 1 && current.Value > 0) _commitHeight = current.Value;
        FilesPane.RowDefinitions[4].MinHeight = 100;
        FilesPane.RowDefinitions[4].MaxHeight = max;
        _commitAppliedHeight = Math.Min(_commitHeight, max);
        FilesPane.RowDefinitions[4].Height = new(_commitAppliedHeight);
        EditorDetailFooter.IsVisible = false;
        EmptyCommitHint.IsVisible = _model.NavigationEntries(1).Count == 0;
        var historyLayout = _historyPanePreferences.Resolve(GitLogWorkspace.Bounds.Width);
        _historyWide = historyLayout.Wide;
        _historyLeftVisible = historyLayout.Branches;
        _historyRightVisible = historyLayout.Files;
        var columns = GitLogWorkspace.ColumnDefinitions;
        if (columns[0].Width.IsAbsolute && columns[0].Width.Value > 0 && columns[0].Width.Value != _gitAppliedBranchWidth) _gitBranchWidth = columns[0].Width.Value;
        if (columns[4].Width.IsAbsolute && columns[4].Width.Value > 0 && columns[4].Width.Value != _gitAppliedFilesWidth) _gitFilesWidth = columns[4].Width.Value;
        columns[0].Width = new(_historyLeftVisible ? Math.Clamp(_gitBranchWidth, 140, 300) : 0);
        columns[1].Width = new(_historyLeftVisible ? 4 : 0);
        columns[4].Width = new(_historyRightVisible ? Math.Clamp(_gitFilesWidth, 180, Math.Max(180, GitLogWorkspace.Bounds.Width * .35)) : 0);
        columns[3].Width = new(_historyRightVisible ? 4 : 0);
        _gitAppliedBranchWidth = columns[0].Width.Value; _gitAppliedFilesWidth = columns[4].Width.Value;
        HistoryBranchesPane.IsVisible = HistoryBranchSplitter.IsVisible = _historyLeftVisible;
        RightWorkspaceSplitter.IsVisible = _historyRightVisible;
        HistoryDetailsPanel.IsVisible = HistoryDetailsSplitter.IsVisible = _historyWide;
        AuxiliaryLayout.RowDefinitions[2].Height = new(_historyWide ? 4 : 0);
        AuxiliaryLayout.RowDefinitions[3].Height = new(_historyWide ? 110 : 0);
        var stacked = historyLayout.Stacked;
        CommitList.Classes.Set("compact", stacked);
        HistoryBranchesButton.IsVisible = !stacked;
        GitLogWorkspace.RowDefinitions = stacked ? new("*,4,*") : new("*");
        Grid.SetRow(GitLogCenter, 0); Grid.SetColumn(GitLogCenter, stacked ? 0 : 2); Grid.SetColumnSpan(GitLogCenter, stacked ? 5 : 1);
        Grid.SetRow(HistoryFilesPane, stacked ? 2 : 0); Grid.SetColumn(HistoryFilesPane, stacked ? 0 : 4); Grid.SetColumnSpan(HistoryFilesPane, stacked ? 5 : 1);
        Grid.SetRow(RightWorkspaceSplitter, stacked ? 1 : 0); Grid.SetColumn(RightWorkspaceSplitter, stacked ? 0 : 3); Grid.SetColumnSpan(RightWorkspaceSplitter, stacked ? 5 : 1);
        RightWorkspaceSplitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        HistoryFilesPane.IsVisible = _historyRightVisible;
        if (stacked)
        {
            foreach (var index in new[] { 0, 1, 3, 4 }) columns[index].Width = new(0);
            HistoryBranchesPane.IsVisible = HistoryBranchSplitter.IsVisible = false;
            GitLogWorkspace.RowDefinitions[1].Height = new(_historyRightVisible ? 4 : 0);
            GitLogWorkspace.RowDefinitions[2].Height = _historyRightVisible ? new(1, GridUnitType.Star) : new(0);
        }
    }
    private void OpenTool(int tool, bool toggle, bool focus)
    {
        if (tool is < 0 or > 3) return;
        _dock?.Show((ToolId)tool, toggle, focus); UpdateNavigationLayout(); UpdateEditorFocusButton();
    }
    private void FocusEditor()
    {
        if (!WindowsWindowCommands.CanFocus(this)) return;
        Activate(); FocusManager?.Focus(null); _webView?.Focus(); _ = InvokeEditorAsync("window.gitx.focus()");
    }
    private void ToggleEditorFocusClicked(object? sender, RoutedEventArgs e)
    { _dock?.ToggleEditorFocus(); UpdateEditorFocusButton(); FocusEditor(); }
    private void UpdateEditorFocusButton()
    {
        var label = _dock?.EditorFocused == true ? "还原工具窗口" : "聚焦代码 · 临时收起停靠工具窗口";
        ToolTip.SetTip(EditorFocusButton, label);
        Avalonia.Automation.AutomationProperties.SetName(EditorFocusButton, label);
        EditorFocusButton.Classes.Set("active", _dock?.EditorFocused == true);
    }
    private void ToggleVisibleChanges() => _model.SetFilesIncluded(_navigation.Rows.Where(row => row.IsFile && row.CanInclude).Select(row => row.Path));
    private void HideLeftToolClicked(object? sender, RoutedEventArgs e) { _dock?.Hide(ToolId.Commit); FocusEditor(); }
    private void HideBottomToolClicked(object? sender, RoutedEventArgs e) { _dock?.HideFocused(); FocusEditor(); }
    private void GitLogClicked(object? sender, RoutedEventArgs e) => OpenTool(2, false, true);
    private void FileHistoryTabClicked(object? sender, RoutedEventArgs e) => OpenTool(3, false, true);
    private void ToolFilterChanged(object? sender, TextChangedEventArgs e)
    { _toolFilters[1] = FilterInput.Text ?? ""; if (_toolFilters[1].Length > 0) _commitFilterVisible = true; UpdateNavigationLayout(); QueueNavigationRefresh(); }
}
