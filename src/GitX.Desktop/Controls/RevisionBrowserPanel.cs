using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop.Controls;

internal sealed class RevisionBrowserPanel : Grid, IDisposable
{
    private readonly RevisionBrowser _model = new();
    private readonly ListBox _files = new() { Classes = { "file-navigation" }, Background = Brushes.Transparent, BorderThickness = new(0) };
    private readonly TextBlock _location = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new(8) };
    private readonly Button _up = new() { Classes = { "toolbar", "icon-button" }, Content = IconContent.Icon("UpIcon") };
    private readonly Button _more = new() { Content = "加载更多", HorizontalAlignment = HorizontalAlignment.Center, Margin = new(8) };
    private readonly Button _retry = new() { Content = "重试", HorizontalAlignment = HorizontalAlignment.Center };
    public event Action? CloseRequested;
    public event Action<string, string>? OpenRequested;
    public string Root => _model.Root;
    public string Sha => _model.Sha;

    public RevisionBrowserPanel()
    {
        RowDefinitions = new("Auto,Auto,*,Auto,Auto,Auto");
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(8, 4) };
        header.Children.Add(_location);
        var close = new Button { Content = "返回工作区", Classes = { "toolbar" } };
        close.Click += (_, _) => CloseRequested?.Invoke(); header.Children.Add(close); Grid.SetColumn(close, 1); Children.Add(header);
        var path = new DockPanel { Margin = new(8, 0, 8, 4) };
        AutomationProperties.SetName(_up, "上一级目录"); ToolTip.SetTip(_up, "上一级目录 · Alt+Up");
        _up.Click += async (_, _) => await UpAsync(); path.Children.Add(_up); DockPanel.SetDock(_up, Dock.Left);
        var directory = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        path.Children.Add(directory); Children.Add(path); Grid.SetRow(path, 1);
        _files.ItemTemplate = new FuncDataTemplate<RevisionTreeEntry>((entry, _) =>
        {
            if (entry is null) return new Border();
            var row = new Grid { ColumnDefinitions = new("20,*,Auto"), Margin = new(4, 2) };
            row.Children.Add(IconContent.Icon(entry.Directory ? "FolderIcon" : "FileIcon"));
            var name = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name); Grid.SetColumn(name, 1);
            var kind = new TextBlock { Text = entry.Submodule ? "子模块" : entry.SymbolicLink ? "链接" : "", Classes = { "muted" }, Margin = new(6, 0) };
            row.Children.Add(kind); Grid.SetColumn(kind, 2); ToolTip.SetTip(row, entry.Path); return row;
        });
        AutomationProperties.SetName(_files, "历史修订文件");
        _files.DoubleTapped += async (_, _) => await OpenSelectedAsync();
        _files.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(); }
            else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.Alt) { e.Handled = true; await UpAsync(); }
        }, RoutingStrategies.Tunnel);
        Children.Add(_files); Grid.SetRow(_files, 2);
        _more.Click += async (_, _) => await _model.LoadMoreAsync(); Children.Add(_more); Grid.SetRow(_more, 3);
        Children.Add(_message); Grid.SetRow(_message, 4);
        _retry.Click += async (_, _) => await _model.RetryAsync(); Children.Add(_retry); Grid.SetRow(_retry, 5);
        _model.PropertyChanged += (_, _) =>
        {
            var selected = (_files.SelectedItem as RevisionTreeEntry)?.Path;
            _files.ItemsSource = _model.Entries; _files.SelectedItem = _model.Entries.FirstOrDefault(e => e.Path == selected);
            _location.Text = (_model.Sha.Length >= 8 ? _model.Sha[..8] : "修订") + " · 只读";
            ToolTip.SetTip(_location, _model.Root + "\n" + _model.Sha);
            directory.Text = _model.Path.Length == 0 ? "/" : _model.Path; ToolTip.SetTip(directory, directory.Text);
            _files.IsEnabled = !_model.Loading; _up.IsEnabled = !_model.Loading && _model.Path.Length > 0;
            _more.IsVisible = _model.HasMore; _more.IsEnabled = !_model.Loading;
            _message.Text = _model.Error.Length > 0 ? _model.Error : _model.Loading ? "正在读取目录…" : _model.Entries.Count == 0 ? "空目录" : $"已显示 {_model.Entries.Count} 项";
            _retry.IsVisible = _model.Error.Length > 0; _retry.IsEnabled = !_model.Loading;
        };
    }
    public Task OpenAsync(string root, string sha) => _model.OpenAsync(root, sha);
    public void FocusFiles() => _files.Focus();
    private Task UpAsync() => _model.Path.Length == 0 ? Task.CompletedTask : _model.NavigateAsync(_model.Path.Contains('/') ? _model.Path[.._model.Path.LastIndexOf('/')] : "");
    private async Task OpenSelectedAsync()
    {
        if (_model.Loading || _files.SelectedItem is not RevisionTreeEntry entry) return;
        if (entry.Directory) await _model.NavigateAsync(entry.Path);
        else if (entry.Submodule) _message.Text = "子模块内容属于独立仓库，此处只展示 Git 记录。";
        else OpenRequested?.Invoke(entry.Path, _model.Sha);
    }
    public void Dispose() => _model.Dispose();
}
