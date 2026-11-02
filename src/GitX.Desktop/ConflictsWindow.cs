using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

internal sealed class ConflictsWindow : Window
{
    private readonly string _root;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListBox _files = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    private readonly Button _merge = new() { Content = "合并…", Classes = { "primary", "dialog-action" } };
    private readonly Button _refresh = new() { Content = "刷新", Classes = { "dialog-action" } };
    private MergeWindow? _child;
    private bool _loading, _closed;

    public ConflictsWindow(string root, string? path = null, bool openSelected = false)
    {
        _root = root; Title = "冲突文件 — Grove"; Width = 880; Height = 480; MinWidth = 640; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _files.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(ListBoxItem.PaddingProperty, new Thickness(4, 0)) } });
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90,90"), Margin = new(5,0) };
        header.Children.Add(new TextBlock { Text = "文件", Classes = { "dialog-label" } });
        var left = new TextBlock { Text = "左侧版本", Classes = { "dialog-label" } }; Grid.SetColumn(left, 1); header.Children.Add(left);
        var right = new TextBlock { Text = "右侧版本", Classes = { "dialog-label" } }; Grid.SetColumn(right, 2); header.Children.Add(right); grid.Children.Add(header);
        _files.ItemTemplate = new FuncDataTemplate<ConflictFile>((file, _) =>
        {
            if (file is null) return new Border();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90,90"), MinHeight = 28 };
            var name = new TextBlock { Text = file.Path, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; ToolTip.SetTip(name, file.Path); row.Children.Add(name);
            var l = new TextBlock { Text = file.Left, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(l, 1); row.Children.Add(l);
            var r = new TextBlock { Text = file.Right, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(r, 2); row.Children.Add(r); return row;
        });
        _files.SelectionChanged += (_, _) => _merge.IsEnabled = !_loading && _files.SelectedItem is ConflictFile;
        _files.DoubleTapped += async (_, _) => await MergeSelectedAsync();
        Grid.SetRow(_files, 1); grid.Children.Add(_files);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 }; footer.Children.Add(_status);
        Grid.SetColumn(_refresh, 1); footer.Children.Add(_refresh); Grid.SetColumn(_merge, 2); footer.Children.Add(_merge);
        var close = new Button { Content = "关闭", Classes = { "dialog-action" } }; Grid.SetColumn(close, 3); footer.Children.Add(close); close.Click += (_, _) => Close();
        var footerFrame = DialogChrome.Footer(footer); Grid.SetRow(footerFrame, 2); grid.Children.Add(footerFrame); Content = grid; DialogChrome.Apply(this);
        _refresh.Click += async (_, _) => await RefreshAsync((_files.SelectedItem as ConflictFile)?.Path);
        _merge.Click += async (_, _) => await MergeSelectedAsync();
        Opened += async (_, _) => { await RefreshAsync(path); if (openSelected && !_closed) await MergeSelectedAsync(); };
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.F5) { e.Handled = true; await RefreshAsync((_files.SelectedItem as ConflictFile)?.Path); }
            else if (e.Key == Key.Enter && _files.IsKeyboardFocusWithin) { e.Handled = true; await MergeSelectedAsync(); }
        }, RoutingStrategies.Tunnel);
        Closing += (_, e) => { if (_child is not null) { e.Cancel = true; _child.Activate(); } };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }
    private async Task RefreshAsync(string? selected)
    {
        if (_loading || _closed) return;
        _loading = true; _refresh.IsEnabled = _merge.IsEnabled = false; _status.Text = "正在读取冲突…";
        try
        {
            var files = await ConflictReader.ListAsync(_root, _lifetime.Token);
            if (_closed) return;
            if (files.Count == 0) { Close(); return; }
            _files.ItemsSource = files; _files.SelectedItem = files.FirstOrDefault(f => f.Path == selected) ?? files[0];
            _status.Text = $"剩余 {files.Count} 个冲突文件";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally { _loading = false; if (!_closed) { _refresh.IsEnabled = true; _merge.IsEnabled = _files.SelectedItem is ConflictFile; } }
    }
    private async Task MergeSelectedAsync()
    {
        if (_loading || _closed || _child is not null || _files.SelectedItem is not ConflictFile file) return;
        _child = new MergeWindow(_root, file.Path);
        try { await _child.ShowDialog(this); }
        finally { _child = null; if (!_closed) await RefreshAsync(file.Path); }
    }
}
