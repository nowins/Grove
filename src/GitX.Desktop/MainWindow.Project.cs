using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly Grid _projectPanel = new() { RowDefinitions = new("Auto,Auto,*,Auto") };
    private readonly ListBox _projectList = new() { Classes = { "file-navigation" }, Focusable = true, FocusAdorner = null, Background = Brushes.Transparent, BorderThickness = new(0), AutoScrollToSelectedItem = false };
    private readonly TextBox _projectFilter = new() { Classes = { "search" }, PlaceholderText = "筛选文件路径…", Margin = new(0, 0, 4, 0) };
    private readonly TextBlock _projectEmpty = new() { Text = "没有可显示的文件", Margin = new(12), Classes = { "muted" }, IsHitTestVisible = false };
    private readonly FileNavigation _projectNavigation = new();
    private string? _projectRoot, _projectSelected;
    private bool _syncingProject, _projectFilterVisible, _projectContextPress;
    private readonly TextBlock _projectNotice = new() { Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap, Margin = new(8, 0, 8, 6), IsVisible = false };
    private readonly Button _projectFilterToggle = new() { Classes = { "toolbar", "icon-button", "log-filter" }, Content = IconContent.Icon("FilterIcon") };
    private void InitializeProjectTool()
    {
        AddFileFilterClear(_projectFilter);
        var header = new DockPanel { Margin = new(8, 4, 4, 4) }; var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        AutomationProperties.SetName(_projectFilterToggle, "筛选项目文件"); ToolTip.SetTip(_projectFilterToggle, "筛选项目文件 · 项目树 Ctrl+F");
        _projectFilterToggle.Click += (_, _) => ToggleProjectFilter(); actions.Children.Add(_projectFilterToggle);
        var reveal = new Button { Classes = { "toolbar", "icon-button" }, Content = IconContent.Icon("RevealIcon") };
        AutomationProperties.SetName(reveal, "在项目树定位当前文件"); ToolTip.SetTip(reveal, "定位当前文件 · Ctrl+Shift+E");
        reveal.Click += (_, _) => RevealProjectFile(); actions.Children.Add(reveal);
        foreach (var (icon, label, filesOnly) in new[] { ("FileIcon", "查找文件 · Ctrl+Shift+N", true), ("SearchIcon", "搜索代码 · Ctrl+Shift+F", false) })
        {
            var button = new Button { Classes = { "toolbar", "icon-button" }, Content = IconContent.Icon(icon) };
            ToolTip.SetTip(button, label); AutomationProperties.SetName(button, label); button.Click += async (_, _) => await OpenCodeSearchAsync(filesOnly); actions.Children.Add(button);
        }
        header.Children.Add(_projectFilter); AutomationProperties.SetName(_projectFilter, "筛选项目文件路径");
        _projectPanel.Children.Add(header);
        _projectPanel.Children.Add(_projectNotice); Grid.SetRow(_projectNotice, 1);
        var body = new Grid(); body.Children.Add(_projectList); body.Children.Add(_projectEmpty); _projectPanel.Children.Add(body); Grid.SetRow(body, 2);
        var shortcuts = new TextBlock { Text = OperatingSystem.IsMacOS() ? "⌘1 项目 · ⌘2 提交 · ⌘3 Git" : "Ctrl+1 项目 · Ctrl+2 提交 · Ctrl+3 Git", Classes = { "muted" }, FontSize = UiTypography.SecondarySize, Margin = new(8, 4) };
        _projectPanel.Children.Add(shortcuts); Grid.SetRow(shortcuts, 3);
        _projectList.ItemTemplate = FileList.ItemTemplate; _projectList.ItemsSource = _projectNavigation.Rows;
        _projectFilter.TextChanged += (_, _) => { _projectNotice.IsVisible = false; if (!string.IsNullOrEmpty(_projectFilter.Text)) _projectFilterVisible = true; UpdateProjectFilter(); RefreshProjectTool(false); };
        _projectFilter.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true; if (string.IsNullOrEmpty(_projectFilter.Text)) _projectFilterVisible = false;
            UpdateProjectFilter(); NativeKeyboardFocus.TryFocusControl(this, _projectList);
        };
        UpdateProjectFilter();
        _model.NavigationChanged += () => RefreshProjectTool(true);
        _projectList.SelectionChanged += async (_, _) =>
        {
            if (_syncingProject || _projectList.SelectedItem is not FileNavigationRow { Entry: { } entry }) return;
            _projectSelected = entry.Path; await _model.OpenEditorEntryAsync(entry);
        };
        _projectList.PointerReleased += async (_, e) =>
        {
            if (_projectContextPress) { _projectContextPress = false; return; }
            if (e.InitialPressMouseButton != MouseButton.Left || (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true) return;
            if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is FileNavigationRow { Entry: { } entry } && entry != _model.SelectedEntry)
                await _model.OpenEditorEntryAsync(entry);
        };
        _projectList.DoubleTapped += async (_, e) =>
        {
            if (_projectList.SelectedItem is FileNavigationRow { CanExpand: true } folder) { ToggleProjectDirectory(folder); return; }
            e.Handled = true; await ActivateProjectFileAsync();
        };
        _projectList.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (_projectList.SelectedItem is FileNavigationRow context && (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
            { e.Handled = true; ShowProjectLocationMenu(_projectList.ContainerFromIndex(_projectList.SelectedIndex) ?? _projectList, context, false); return; }
            if (NavigateListPage(_projectList, e)) return;
            if (_projectList.SelectedItem is not FileNavigationRow row) return;
            if (row.CanExpand && (e.Key is Key.Enter or Key.Space || e.Key == Key.Right && !row.IsExpanded || e.Key == Key.Left && row.IsExpanded))
            { e.Handled = true; ToggleProjectDirectory(row); }
            else if (e.Key == Key.Left && _projectNavigation.Parent(row) is { } parent) { _projectList.SelectedItem = parent; e.Handled = true; }
            else if (e.Key == Key.Enter && row.Entry is not null) { e.Handled = true; await ActivateProjectFileAsync(); }
        }, RoutingStrategies.Tunnel);
        _projectList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var point = e.GetCurrentPoint(_projectList).Properties;
            _projectContextPress = point.IsRightButtonPressed || OperatingSystem.IsMacOS() && point.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!_projectContextPress) return;
            var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            if (row?.DataContext is not FileNavigationRow target || _model.Root is null) return;
            e.Handled = true;
            _syncingProject = true;
            try { _projectList.SelectedItem = target; } finally { _syncingProject = false; }
            row.Focus(); ShowProjectLocationMenu(row, target, true);
        }, RoutingStrategies.Tunnel);
    }
    private void RefreshProjectTool(bool preserveScroll)
    {
        var same = _projectRoot == _model.Root;
        if (!same) { _projectSelected = null; _projectNotice.IsVisible = false; }
        var offset = _projectList.Scroll?.Offset;
        var previous = _projectList.SelectedItem as FileNavigationRow;
        var filter = _projectFilter.Text ?? "";
        var entries = _model.NavigationEntries(0).Where(e => filter.Length == 0 || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        _syncingProject = true;
        try
        {
            var selected = entries.FirstOrDefault(e => e.Path == _projectSelected);
            _projectNavigation.Update(_model.Root, entries, 0, filter.Length > 0, selected, revealSelected: !(same && previous?.CanExpand == true));
            _projectList.SelectedItem = same && previous?.CanExpand == true ? _projectNavigation.Rows.FirstOrDefault(r => r.Key == previous.Key)
                : selected is null ? null : _projectNavigation.ShowFile(selected);
            _projectEmpty.IsVisible = entries.Length == 0;
            _projectEmpty.Text = string.IsNullOrWhiteSpace(filter) ? "没有可显示的文件" : "没有匹配的文件";
            _projectRoot = _model.Root;
            if (preserveScroll && same && offset is { } position && _projectList.Scroll is { } scroll) scroll.Offset = position;
        }
        finally { _syncingProject = false; }
    }
    private void ToggleProjectDirectory(FileNavigationRow row)
    {
        _syncingProject = true;
        try { _projectNavigation.Toggle(row); _projectList.SelectedItem = _projectNavigation.Rows.FirstOrDefault(r => r.Key == row.Key); }
        finally { _syncingProject = false; }
        NativeKeyboardFocus.TryFocusControl(this, _projectList);
    }
    private async Task ActivateProjectFileAsync()
    {
        if (_projectList.SelectedItem is not FileNavigationRow { Entry: { } entry }) return;
        var root = _model.Root; var epoch = _inputEpoch;
        var owner = TopLevel.GetTopLevel(_projectPanel) as Window;
        await _model.OpenEditorEntryAsync(entry);
        if (_closed || root != _model.Root || epoch != _inputEpoch || owner?.IsActive != true) return;
        Activate(); RequestSourceFocus();
    }

    private void UpdateProjectFilter()
    {
        var active = !string.IsNullOrEmpty(_projectFilter.Text);
        _projectFilter.IsVisible = _projectFilterVisible || active;
        _projectFilterToggle.Classes.Set("active", active);
        ToolTip.SetTip(_projectFilterToggle, active ? "项目筛选生效：" + _projectFilter.Text : "筛选项目文件 · 项目树 Ctrl+F");
    }
    private void ToggleProjectFilter()
    {
        _dock?.Show(ToolId.Project, focus: false);
        _projectFilterVisible = !string.IsNullOrEmpty(_projectFilter.Text) || !_projectFilterVisible;
        UpdateProjectFilter(); NativeKeyboardFocus.TryFocusControl(this, _projectFilter.IsVisible ? _projectFilter : _projectList);
    }
    private void RevealProjectFile() => RevealProjectFile(_model.CurrentProjectFile,
        _model.IsHistory || _model.SourceTabs.Any(tab => tab.Id == _model.ActiveSourceTabId && tab.Revision is not null));
    private void RevealProjectFile(FileHistoryTarget? target, bool historical)
    {
        _dock?.Show(ToolId.Project, focus: false);
        var result = ProjectFileReveal.Resolve(_model.Root, target, _model.NavigationEntries(0), _projectFilter.Text ?? "");
        if (result.Entry is not { } entry)
        {
            _projectNotice.Text = result.Message; _projectNotice.IsVisible = true;
            if (result.HiddenByFilter) { _projectFilterVisible = true; UpdateProjectFilter(); NativeKeyboardFocus.TryFocusControl(this, _projectFilter); }
            return;
        }
        if (_model.Root is not { } root || !File.Exists(Path.Combine(root, entry.Path)))
        { _projectNotice.Text = "文件已不在当前工作树中，请刷新；历史版本不会被替换。"; _projectNotice.IsVisible = true; return; }
        _syncingProject = true;
        try
        {
            var row = _projectNavigation.ShowFile(entry);
            if (row is null) return;
            _projectSelected = entry.Path; _projectList.SelectedItem = row; _projectList.ScrollIntoView(row);
            _projectNotice.Text = "已定位当前工作树中的同路径文件；编辑器版本保持不变。";
            _projectNotice.IsVisible = historical;
        }
        finally { _syncingProject = false; }
        NativeKeyboardFocus.TryFocusControl(this, _projectList);
    }
}
