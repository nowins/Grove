using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly RecentRepositoryStore _recentRepositories = new(LocalDataDirectory);
    private PopupFlyoutBase? _shellPopup;
    private Control? _shellAnchor;
    private long _shellGeneration;
    private long _repositoryChoiceGeneration;

    private void InitializeShell()
    {
        InitializePlatformMenus();
        InitializeRecentFiles();
        _model.RepositoryOpened += async root =>
        {
            CloseShellPopup();
            try { await _recentRepositories.RememberAsync(root); }
            catch (Exception ex) { if (!_closed && _model.Root == root) _model.ReportError("最近项目未能保存：" + ex.Message); }
        };
        _model.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ViewModels.MainViewModel.IsWriting) && _model.IsWriting) CloseShellPopup(); };
        Closed += (_, _) => CloseShellPopup();
    }

    private void CloseShellPopup(bool restoreFocus = false)
    {
        var generation = ++_shellGeneration;
        var popup = _shellPopup; var anchor = _shellAnchor;
        _shellPopup = null; _shellAnchor = null;
        anchor?.Classes.Remove("popup-open"); popup?.Hide();
        if (restoreFocus && !_closed && anchor is not null && GetTopLevel(anchor) is Window owner)
            Dispatcher.UIThread.Post(() => { if (!_closed && generation == _shellGeneration && owner.IsActive) NativeKeyboardFocus.TryFocusControl(owner, anchor); });
    }

    private bool ToggleShellPopup(Control anchor)
    {
        var same = _shellAnchor == anchor && _shellPopup?.IsOpen == true;
        CloseShellPopup(); return same;
    }

    private void ShowShellPopup(PopupFlyoutBase popup, Control anchor, PlacementMode placement = PlacementMode.BottomEdgeAlignedLeft, bool restoreFocusOnDismiss = true)
    {
        _shellPopup = popup; _shellAnchor = anchor;
        anchor.Classes.Add("popup-open");
        var ime = OperatingSystem.IsWindows() && popup is Flyout { Content: Control content } && GetTopLevel(anchor) is Window owner
            ? new WindowsPopupIme(owner, content) : null;
        popup.Placement = placement;
        popup.Closed += (_, _) =>
        {
            ime?.Dispose();
            if (_shellPopup != popup) return;
            _shellPopup = null; _shellAnchor = null; ++_shellGeneration;
            anchor.Classes.Remove("popup-open");
            if (restoreFocusOnDismiss && !_closed && IsActive) anchor.Focus();
        };
        popup.ShowAt(anchor);
    }

    private Button ShellAction(string text, string? icon, Action action, string? disabled = null)
    {
        var content = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("20,*") };
        if (icon is not null) content.Children.Add(IconContent.Icon(icon));
        var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); content.Children.Add(label);
        var button = new Button { Content = content,
            Classes = { "shell-action" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = disabled is null };
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, disabled ?? text);
        button.Click += (_, _) => action(); return button;
    }

    private static void FocusShellList(ListBox list)
    {
        if (list.SelectedIndex < 0) return;
        list.ScrollIntoView(list.SelectedIndex);
        (list.ContainerFromIndex(list.SelectedIndex) ?? list).Focus();
    }

    private bool CanChangeRepository()
    {
        var reason = _model.IsWriting ? "Git 操作进行中，请完成后再切换仓库。" :
            _mergeWindow is not null ? "请先关闭当前冲突窗口。" :
            _commentBusy || _commentDraft is not null ? "请先保存或取消当前行评论输入。" : null;
        if (reason is null) return true;
        _model.ReportError(reason); return false;
    }

    private async Task OpenRepositoryAsync(string path)
    {
        if (!CanChangeRepository()) return;
        var generation = ++_repositoryChoiceGeneration;
        CloseShellPopup();
        try
        {
            // Missing recent paths or a mistyped destination must not clear the current editor/repository.
            var root = await RepositoryReader.ResolveWorkingDirectoryAsync(path.Trim());
            if (_closed || generation != _repositoryChoiceGeneration || !CanChangeRepository()) return;
            SwitchView(0); await _model.OpenAsync(root);
        }
        catch (Exception ex) { if (!_closed && generation == _repositoryChoiceGeneration) _model.ReportError(ex.Message); }
    }

    private sealed record RecentProject(string Root, bool Current)
    {
        public string Name => System.IO.Path.GetFileName(Root.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Root;
    }

    private async void RepositoriesClicked(object? sender, RoutedEventArgs e)
    {
        if (ToggleShellPopup(RepositoryButton)) return;
        var generation = _shellGeneration; var root = _model.Root;
        try
        {
            var recent = await _recentRepositories.ReadAsync();
            if (_closed || generation != _shellGeneration || root != _model.Root) return;
            var projects = (root is null ? recent : recent.Prepend(root))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => new RecentProject(p, p == root)).ToArray();
            var input = new TextBox { PlaceholderText = "搜索最近项目，或输入仓库完整路径", Classes = { "search" } };
            AutomationProperties.SetName(input, "搜索项目或输入仓库路径");
            var list = new ListBox { MaxHeight = Math.Clamp(Bounds.Height - 250, 160, 420), Background = Brushes.Transparent, BorderThickness = new(0), ItemsSource = projects };
            list.ItemTemplate = new FuncDataTemplate<RecentProject>((project, _) =>
            {
                if (project is null) return new Border();
                var row = new StackPanel { Spacing = 2, Margin = new(0,2) };
                row.Children.Add(new TextBlock { Text = project.Name + (project.Current ? "  · 当前项目" : ""), TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = project.Root, Classes = { "muted" }, FontSize = UiTypography.SecondarySize, TextTrimming = TextTrimming.CharacterEllipsis });
                ToolTip.SetTip(row, project.Root); AutomationProperties.SetName(row, project.Name + " " + project.Root); return row;
            });
            var empty = new TextBlock { Text = "没有匹配的最近项目，可输入完整路径打开。", Classes = { "muted" }, Margin = new(8) };
            var panel = new StackPanel { Width = Math.Min(410, Bounds.Width - 100), Spacing = 0 };
            input.Margin = new(4,4,4,6);
            var flyout = new Flyout { Content = panel }; flyout.FlyoutPresenterClasses.Add("shell-popup");
            panel.Children.Add(ShellAction("打开本地仓库…", "FolderIcon", () => { CloseShellPopup(); _ = PickRepositoryAsync(); }));
            panel.Children.Add(input); panel.Children.Add(new TextBlock { Text = "当前与最近项目", Classes = { "muted" }, Margin = new(8,5) });
            panel.Children.Add(list); panel.Children.Add(empty);
            var openPath = ShellAction("打开输入的仓库路径", "FolderIcon", () => _ = OpenRepositoryAsync(input.Text ?? ""));
            panel.Children.Add(openPath);
            if (root is not null)
            {
                panel.Children.Add(new Separator());
                panel.Children.Add(ShellAction("工作树 · Worktrees", "BranchIcon", () => _ = OpenWorktreesAsync()));
                panel.Children.Add(ShellAction("仓库详情…", "FolderIcon", () => { CloseShellPopup(); ShowRepositoryDetails(); }));
            }
            void Filter()
            {
                var rows = projects.Where(p => p.Root.Contains(input.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
                list.ItemsSource = rows; list.SelectedIndex = rows.Length > 0 ? 0 : -1; empty.IsVisible = rows.Length == 0;
                openPath.IsVisible = System.IO.Path.IsPathFullyQualified(input.Text ?? "");
            }
            input.TextChanged += (_, _) => Filter(); Filter();
            async Task OpenSelected()
            {
                var path = (list.SelectedItem as RecentProject)?.Root;
                if (!string.IsNullOrWhiteSpace(path)) await OpenRepositoryAsync(path);
            }
            input.KeyDown += (_, key) =>
            {
                if (key.Key == Key.Down) { key.Handled = true; FocusShellList(list); }
            };
            list.AddHandler(KeyDownEvent, async (_, key) => { if (key.Key == Key.Enter) { key.Handled = true; await OpenSelected(); } }, RoutingStrategies.Tunnel);
            list.PointerReleased += async (_, pointer) =>
            {
                if (pointer.InitialPressMouseButton != MouseButton.Left || (pointer.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is null) return;
                await OpenSelected();
            };
            flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => { if (flyout.IsOpen) input.Focus(); }, DispatcherPriority.Input);
            ShowShellPopup(flyout, RepositoryButton);
        }
        catch (Exception ex) { if (!_closed && generation == _shellGeneration) _model.ReportError(ex.Message); }
    }

    private void MainMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsMacOS()) return;
        var anchor = sender as Control ?? RepositoryButton;
        if (ToggleShellPopup(anchor)) return;
        var menu = new MenuFlyout();
        MenuItem Group(string text) { var group = new MenuItem { Header = text }; menu.Items.Add(group); return group; }
        void Item(MenuItem group, string text, Action action, string? icon = null, bool enabled = true, string? shortcut = null)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            if (icon is not null) item.Icon = IconContent.Icon(icon);
            if (shortcut is not null) item.InputGesture = KeyGesture.Parse(shortcut);
            item.Click += (_, _) => { CloseShellPopup(); action(); }; group.Items.Add(item);
        }
        foreach (var group in ShellCommands.WindowMenu(false).GroupBy(c => c.Group))
        {
            var parent = Group(group.Key);
            foreach (var command in group)
            {
                if (command.SeparatorBefore && parent.Items.Count > 0) parent.Items.Add(new Separator());
                var id = command.Id;
                Item(parent, command.Header, () => RunShellCommand(id, InteractionWindow), command.Icon,
                    CanRunShellCommand(id, InteractionWindow), command.WindowsGesture);
            }
        }
        ShowShellPopup(menu, anchor);
    }

    private async void RemoteToolbarClicked(object? sender, RoutedEventArgs e) => await OpenRemoteMenuAsync(RemoteButton);
    private void ShowRepositoryDetails()
    {
        if (_model.Root is not { } root) return;
        ShowShellText("仓库详情 · " + _model.RepositoryName, _model.RepositoryDetail, root);
    }
    private void ShowAbout()
    {
        var dialog = new Window { Title = "关于 Grove", Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Spacing = 12 };
        var identity = new StackPanel { Spacing = 4 };
        identity.Children.Add(new TextBlock { Text = "Grove", Classes = { "dialog-title" } });
        identity.Children.Add(new TextBlock { Text = "本地 Git 客户端", Classes = { "dialog-label" } });
        body.Children.Add(identity);
        var location = new StackPanel { Spacing = 4 };
        location.Children.Add(new TextBlock { Text = "当前运行位置", Classes = { "dialog-section" } });
        location.Children.Add(new TextBox { Text = AppContext.BaseDirectory, IsReadOnly = true,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)Application.Current!.FindResource("CodeFontFamily")!,
            FontSize = UiTypography.SecondarySize, MinHeight = 0, Padding = new(0), BorderThickness = new(0), Background = Brushes.Transparent });
        body.Children.Add(location);
        var close = new Button { Content = "关闭", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = DialogChrome.Layout(body, DialogChrome.Buttons(close));
        DialogChrome.Apply(dialog);
        dialog.Opened += (_, _) => close.Focus(NavigationMethod.Tab);
        _ = dialog.ShowDialog(this);
    }

    private void ShowShellText(string title, string text, string? copyPath = null)
    {
        var dialog = new Window { Title = title, Width = 580, Height = 390, MinWidth = 420, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new(16), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (copyPath is not null)
        {
            var copy = new Button { Content = "复制仓库路径", Classes = { "dialog-action" } };
            copy.Click += async (_, _) => { if (dialog.Clipboard is { } clipboard) await clipboard.SetTextAsync(copyPath); };
            actions.Children.Add(copy);
        }
        var close = new Button { Content = "关闭", IsCancel = true, Classes = { "dialog-action" } }; close.Click += (_, _) => dialog.Close(); actions.Children.Add(close);
        var footer = DialogChrome.Footer(actions); footer.Margin = new(0,12,0,0);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new(0), Background = Brushes.Transparent });
        dialog.Content = panel; DialogChrome.Apply(dialog); _ = dialog.ShowDialog(this);
    }
    private void RevealRepository()
    {
        if (_model.Root is not { } root) return;
        try { Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); }
        catch (Exception ex) { _model.ReportError(ex.Message); }
    }
}
