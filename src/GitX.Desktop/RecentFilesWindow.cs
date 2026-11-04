using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Desktop.Services;

namespace GitX.Desktop;

// A single transient picker, owned by the active Grove window. Merely moving
// the selection never opens a document or changes its saved view state.
internal sealed class RecentFilesWindow : Window
{
    private readonly TextBox _search = new() { PlaceholderText = "搜索最近文件", Classes = { "search" } };
    private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new(0), AutoScrollToSelectedItem = true };
    private readonly TextBlock _status = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<RecentFile> _files = [];
    private bool _opening, _shown, _closing, _dismissedOutside;
    public bool IsDismissed => _closing;
    public bool RestoreFocusOnClose => !_dismissedOutside;
    public bool OpenedFile { get; private set; }

    public RecentFilesWindow(Func<RecentFile, Task<string?>> open, Func<RecentFile, Task> forget)
    {
        Title = "最近文件"; Width = 620; Height = 420; MinWidth = 0; MinHeight = 0;
        WindowDecorations = WindowDecorations.None; CanResize = CanMinimize = CanMaximize = false;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto"), Margin = new(12), RowSpacing = 8 };
        panel.Children.Add(new TextBlock { Text = "最近文件", Classes = { "dialog-title" } });
        panel.Children.Add(_search); Grid.SetRow(_search, 1);
        panel.Children.Add(_list); Grid.SetRow(_list, 2);
        var footer = new StackPanel { Spacing = 4 };
        footer.Children.Add(_status);
        footer.Children.Add(new TextBlock { Text = "↑ ↓ 选择  ·  Enter 打开  ·  Esc 取消  ·  Delete（列表）移除记录", Classes = { "muted" }, FontSize = UiTypography.SecondarySize });
        panel.Children.Add(footer); Grid.SetRow(footer, 3);
        Content = new Border { Child = panel, BorderThickness = new(1), BorderBrush = (IBrush?)Application.Current?.FindResource("BorderBrush"), CornerRadius = new(6) };
        Background = (IBrush)Application.Current!.FindResource("PanelBrush")!;
        AutomationProperties.SetName(_search, "搜索最近文件"); AutomationProperties.SetName(_list, "当前仓库的最近文件");
        _list.ItemTemplate = new FuncDataTemplate<RecentFile>((file, _) =>
        {
            if (file is null) return new Border();
            var row = new StackPanel { Spacing = 2, Margin = new(4, 3) };
            row.Children.Add(new TextBlock { Text = file.Name + "  ·  " + file.Context, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = file.Path, Classes = { "muted" }, FontSize = UiTypography.SecondarySize, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTip.SetTip(row, file.Path + "\n" + file.Context); return row;
        });
        void Filter()
        {
            var rows = RecentFileStore.Filter(_files, _search.Text ?? ""); _list.ItemsSource = rows; _list.SelectedIndex = rows.Count == 0 ? -1 : 0;
            _status.Text = rows.Count == 0 ? (_files.Count == 0 ? "尚未访问源码文件。" : "没有匹配的最近文件。") : "当前工作树 · 仅记录访问成功的源码文件，历史版本单独标记";
        }
        async Task OpenAsync()
        {
            if (_opening || _list.SelectedItem is not RecentFile file) return;
            _opening = true; _status.Text = "正在打开…";
            try { var error = await open(file); if (error is null) { OpenedFile = true; Dismiss(); } else _status.Text = error; }
            catch (Exception ex) { _status.Text = "打开失败：" + ex.Message; }
            finally { _opening = false; }
        }
        _search.TextChanged += (_, _) => Filter();
        _list.DoubleTapped += async (_, _) => await OpenAsync();
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (_search.IsKeyboardFocusWithin && _search.GetVisualDescendants().OfType<TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText))) return;
            if (e.Key == Key.Escape) { e.Handled = true; if (!_opening) Dismiss(); }
            else if (e.Key == Key.Enter && !e.Handled) { e.Handled = true; await OpenAsync(); }
            else if (e.Key is Key.Down or Key.Up && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true; var count = _list.ItemCount;
                if (count > 0) _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, count - 1);
            }
            else if (e.Key == Key.Delete && _list.IsKeyboardFocusWithin && !_opening && _list.SelectedItem is RecentFile file)
            {
                e.Handled = true;
                try { await forget(file); _files = _files.Where(f => f != file).ToArray(); Filter(); }
                catch (Exception ex) { _status.Text = "记录移除失败：" + ex.Message; }
            }
        }, RoutingStrategies.Tunnel);
        Opened += (_, _) => { _shown = true; FocusSearch(); };
        // A click elsewhere dismisses this transient window; owned modal dialogs
        // are not involved and no file is opened on focus loss.
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => { _closing = true; _shown = false; };
        Deactivated += (_, _) =>
        {
            // Cocoa also sends Deactivated from HandleClosed, after releasing
            // the native proxy. Check visibility on the next dispatch turn and
            // never reenter Close while an existing close is in progress.
            if (!_shown || _opening || _closing) return;
            Dispatcher.UIThread.Post(() => { if (!_closing && !_opening && !IsActive && IsVisible) { _dismissedOutside = true; Dismiss(); } }, DispatcherPriority.Background);
        };
    }
    private void Dismiss()
    {
        if (_closing) return;
        _closing = true; Close();
    }
    public void SetFiles(IReadOnlyList<RecentFile> files)
    {
        _files = files; _list.ItemsSource = RecentFileStore.Filter(files, ""); _list.SelectedIndex = files.Count == 0 ? -1 : 0;
        _status.Text = files.Count == 0 ? "尚未访问源码文件。" : "当前工作树 · 历史版本单独标记";
    }
    public void FocusSearch() { Activate(); _search.Focus(); }
    public async Task ShowPickerAsync(Window owner)
    {
        Width = Math.Min(620, owner.ClientSize.Width - 32); Height = Math.Min(420, owner.ClientSize.Height - 32);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => completion.TrySetResult(); Show(owner); await completion.Task;
    }
}
