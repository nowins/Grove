using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop.Controls;

internal sealed class WorktreePanel : Grid
{
    private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new(0) };
    private readonly TextBlock _location = new() { Classes = { "dialog-hint" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new() { Content = IconContent.Label("RefreshIcon", "刷新"), Classes = { "toolbar" } };
    private readonly Button _open = new() { Content = "打开工作树", Classes = { "dialog-action", "primary" }, IsEnabled = false };
    private bool _busy, _canOpen;
    private readonly Button _remove = new() { Content = "删除…", Classes = { "dialog-action" }, IsEnabled = false };
    public WorktreeSnapshot? Snapshot { get; private set; }
    public event Action? RefreshRequested;
    public event Action<WorktreeSnapshot, WorktreeEntry>? OpenRequested;
    public event Action<WorktreeSnapshot, WorktreeEntry>? RemoveRequested;

    public WorktreePanel()
    {
        RowDefinitions = new("Auto,*,Auto"); RowSpacing = 8; Margin = new(12, 8);
        var header = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        header.Children.Add(_location); header.Children.Add(_refresh); Grid.SetColumn(_refresh, 1);
        header.Children.Add(_open); Grid.SetColumn(_open, 2); Children.Add(header);
        header.Children.Add(_remove); Grid.SetColumn(_remove, 3);
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _open.Click += (_, _) => OpenSelected();
        _remove.Click += (_, _) => { if (_remove.IsEnabled && Snapshot is { } snapshot && _list.SelectedItem is WorktreeEntry entry) RemoveRequested?.Invoke(snapshot, entry); };
        AutomationProperties.SetName(_remove, "检查并确认删除所选关联工作树");
        ToolTip.SetTip(_remove, "先检查本地文件与锁定状态，再确认删除；不支持强制删除。");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _open.IsEnabled) { e.Handled = true; OpenSelected(); } };
        AutomationProperties.SetName(_list, "仓库工作树");
        AutomationProperties.SetName(_refresh, "刷新工作树");
        AutomationProperties.SetName(_open, "打开所选工作树");
        _list.Styles.Add(new Style(s => s.OfType<ListBoxItem>()) { Setters = { new Setter(AutomationProperties.NameProperty, new Binding(nameof(WorktreeEntry.AccessibleName))) } });
        _list.ItemTemplate = new FuncDataTemplate<WorktreeEntry>((entry, _) =>
        {
            if (entry is null) return new Border();
            var row = new StackPanel { Spacing = 4, Margin = new(4) };
            var title = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
            title.Children.Add(new TextBlock { Text = entry.BranchLabel, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            void Badge(string text, string brush) => badges.Children.Add(new TextBlock { Text = text, FontSize = UiTypography.SecondarySize, Foreground = (IBrush)Application.Current!.FindResource(brush)! });
            if (entry.Current) Badge("当前", "AccentBrush");
            if (entry.LockedReason is not null) Badge("已锁定", "MutedBrush");
            if (entry.UnavailableReason is not null) Badge("不可用", "ErrorTextBrush");
            title.Children.Add(badges); Grid.SetColumn(badges, 1); row.Children.Add(title);
            row.Children.Add(new TextBlock { Text = entry.Root, Classes = { "muted" }, FontSize = UiTypography.SecondarySize, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = entry.UnavailableReason ?? (entry.Head is { } sha ? sha[..Math.Min(12, sha.Length)] : "尚无提交"),
                FontFamily = entry.UnavailableReason is null ? (FontFamily)Application.Current!.FindResource("CodeFontFamily")! : UiTypography.Family,
                FontSize = entry.UnavailableReason is null ? UiTypography.GitLogMetaSize : UiTypography.SecondarySize,
                Foreground = (IBrush)Application.Current!.FindResource(entry.UnavailableReason is null ? "MutedBrush" : "ErrorTextBrush")!, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTip.SetTip(row, entry.Root + "\n" + entry.Head + (entry.LockedReason is { } reason ? "\n锁定原因：" + reason : "") + (entry.UnavailableReason is { } unavailable ? "\n" + unavailable : ""));
            return row;
        });
        Children.Add(_list); Grid.SetRow(_list, 1);
        Children.Add(_message); Grid.SetRow(_message, 2);
        Clear(null);
    }

    public void Clear(string? root)
    {
        Snapshot = null; _list.ItemsSource = null; _location.Text = root ?? "尚未打开仓库";
        ToolTip.SetTip(_location, root); _message.Text = root is null ? "打开仓库后可查看关联工作树。" : "正在读取工作树…";
        _message.Foreground = (IBrush)Application.Current!.FindResource("MutedBrush")!; UpdateButtons();
    }
    public void Present(WorktreeSnapshot snapshot)
    {
        var previous = (_list.SelectedItem as WorktreeEntry)?.Root;
        Snapshot = snapshot; _location.Text = snapshot.Root; ToolTip.SetTip(_location, snapshot.Root);
        _list.ItemsSource = snapshot.Entries;
        _list.SelectedItem = snapshot.Entries.FirstOrDefault(e => e.Root == previous) ?? snapshot.Entries.FirstOrDefault(e => e.Current);
        _message.Text = "切换只打开所选目录，原工作树的本地更改保留。锁定状态不妨碍打开；失效或缺失的工作树不可打开。";
        _message.Foreground = (IBrush)Application.Current!.FindResource("MutedBrush")!; UpdateButtons();
    }
    public void SetBusy(bool value) { _busy = value; UpdateButtons(); }
    public void SetCanOpen(bool value) { _canOpen = value; UpdateButtons(); }
    public void ShowError(string message) { _message.Text = message; _message.Foreground = (IBrush)Application.Current!.FindResource("ErrorTextBrush")!; }
    public void FocusList() => _list.Focus();
    private void OpenSelected()
    { if (_open.IsEnabled && Snapshot is { } snapshot && _list.SelectedItem is WorktreeEntry entry) OpenRequested?.Invoke(snapshot, entry); }
    private void UpdateButtons()
    {
        _refresh.IsEnabled = !_busy;
        _open.IsEnabled = !_busy && _canOpen && Snapshot is not null && _list.SelectedItem is WorktreeEntry { Current: false, UnavailableReason: null };
        _remove.IsEnabled = !_busy && _canOpen && Snapshot is not null && _list.SelectedItem is WorktreeEntry { Current: false, Bare: false, UnavailableReason: null, LockedReason: null };
    }
}
