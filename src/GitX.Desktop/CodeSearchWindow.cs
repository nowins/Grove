using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

internal sealed partial class CodeSearchWindow : Window
{
    private readonly string _root;
    private readonly Func<IReadOnlyList<string>> _paths;
    private readonly bool _inventoryTruncated;
    private readonly Func<CodeSearchMatch, Task<string?>> _openMatch;
    private readonly IReadOnlyList<string> _recentPaths;
    private SearchResultList? _visibleMatches;
    private readonly TextBox _query = new() { Classes = { "search" }, MaxLength = 1024 };
    private readonly TextBox _mask = new() { Classes = { "search" }, PlaceholderText = "全部文件；例如 *.cs;*.java", MaxLength = 1024 };
    private readonly CheckBox _case = new() { Content = "区分大小写" }, _word = new() { Content = "整词" }, _regex = new() { Content = "正则" };
    private readonly RadioButton _files = new() { Content = "文件名", GroupName = "CodeSearch" }, _content = new() { Content = "代码内容", GroupName = "CodeSearch" };
    private readonly ListBox _results = new() { Focusable = true, BorderThickness = new(0) };
    private readonly TextBlock _status = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _open = new() { Content = "打开", Classes = { "primary", "dialog-action" }, MinWidth = 72, IsEnabled = false };
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CancellationTokenSource? _read;
    private long _sequence;
    private bool _closed, _opening, _contextInvalid;
    private long _resultSequence = -1;
    private long? _pendingOpen;
    private readonly bool _quick;
    private readonly Func<ShellCommandId, bool> _canRunAction;
    public ShellCommandId? RequestedCommand { get; private set; }
    private Action? _searchOpened;
    public bool OpenedMatch { get; private set; }
    private readonly CodeSearchQuery _initialQuery;
    public CodeSearchQuery Query => IsActionSearch ? _initialQuery : new(_query.Text ?? "", _files.IsChecked == true, _case.IsChecked == true, _word.IsChecked == true, _regex.IsChecked == true, _mask.Text ?? "");

    public CodeSearchWindow(string root, Func<IReadOnlyList<string>> paths, bool inventoryTruncated, CodeSearchQuery query, Func<CodeSearchMatch, Task<string?>> openMatch, bool quick = false, Func<ShellCommandId, bool>? canRunAction = null, Func<bool>? previewContextCurrent = null, IReadOnlyList<string>? recentPaths = null)
    {
        _root = root; _paths = paths; _inventoryTruncated = inventoryTruncated; _openMatch = openMatch; _quick = quick;
        _recentPaths = recentPaths ?? [];
        _canRunAction = canRunAction ?? (_ => false); _initialQuery = query;
        _previewContextCurrent = previewContextCurrent ?? (() => true);
        Title = quick ? "搜索 — Grove · 双击 Shift" : "查找代码 — Grove"; Width = quick ? 680 : 760; Height = quick ? 400 : 470; MinWidth = 540; MinHeight = 310;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _results.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        var panel = new Grid { Margin = new(16), RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 8 };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        modes.Children.Add(_files); modes.Children.Add(_content); panel.Children.Add(modes);
        Grid.SetRow(_query, 1); panel.Children.Add(_query);
        var options = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var option in new[] { _case, _word, _regex }) { option.Margin = new(0, 0, 14, 0); options.Children.Add(option); }
        Grid.SetRow(options, 2); panel.Children.Add(options);
        var scope = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"), ColumnSpacing = 8 };
        scope.Children.Add(new TextBlock { Text = "文件范围", VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(_mask, 1); scope.Children.Add(_mask);
        Grid.SetRow(scope, 3); panel.Children.Add(scope);
        _results.ItemTemplate = new FuncDataTemplate<CodeSearchMatch>((match, _) =>
        {
            if (match is null) return new Border();
            var row = new StackPanel { Spacing = 2, Margin = new(0, 3) };
            var path = new TextBlock { Text = match.Path + (Query.FilesOnly ? "" : $":{match.Line}:{match.Column}"), TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(path); ToolTip.SetTip(row, match.Path + $":{match.Line}:{match.Column}\n" + match.Preview);
            if (!Query.FilesOnly)
            {
                var preview = new TextBlock { Classes = { "muted" }, FontFamily = (FontFamily)Application.Current!.FindResource("CodeFontFamily")!, TextTrimming = TextTrimming.CharacterEllipsis };
                var start = Math.Min(match.PreviewOffset, match.Preview.Length); var end = Math.Min(start + match.Length, match.Preview.Length);
                preview.Inlines!.Add(new Run(match.Preview[..start]));
                preview.Inlines.Add(new Run(match.Preview[start..end]) { Foreground = (IBrush)Application.Current!.FindResource("TextBrush")!, FontWeight = FontWeight.Bold });
                preview.Inlines.Add(new Run(match.Preview[end..])); row.Children.Add(preview);
            }
            AutomationProperties.SetName(row, path.Text + " " + match.Preview); return row;
        });
        var resultArea = new Border { BorderThickness = new(1), BorderBrush = (IBrush)Application.Current!.FindResource("BorderBrush")!, Child = _results };
        Grid.SetRow(resultArea, 4); panel.Children.Add(resultArea);
        var footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        var refresh = new Button { Content = "重新搜索", Classes = { "dialog-action" }, MinWidth = 72 }; ToolTip.SetTip(refresh, "重新读取工作区内容 · F5");
        footer.Children.Add(_status); Grid.SetColumn(refresh, 1); footer.Children.Add(refresh); Grid.SetColumn(_open, 2); footer.Children.Add(_open);
        var cancel = new Button { Content = "取消", Classes = { "dialog-action" }, MinWidth = 72 }; cancel.Click += (_, _) => CloseSearch(); Grid.SetColumn(cancel, 3); footer.Children.Add(cancel);
        var footerFrame = DialogChrome.Footer(footer); footerFrame.Margin = new(0,4,0,0); Grid.SetRow(footerFrame, 5); panel.Children.Add(footerFrame);
        if (quick)
        {
            // Reuse the same query controls and search lifecycle in the compact
            // popup; controls cannot have two visual parents.
            panel.Children.Clear(); modes.Children.Clear();
            footer.Children.Remove(_status);
            resultArea.Child = null;
            Content = BuildQuickContent(options, scope);
        }
        else { Content = panel; DialogChrome.Apply(this); }
        AutomationProperties.SetName(_query, "搜索文本"); AutomationProperties.SetName(_mask, "搜索文件范围"); AutomationProperties.SetName(_results, "代码搜索结果");
        _query.Text = query.Text; _mask.Text = query.FileMask; _case.IsChecked = query.MatchCase; _word.IsChecked = query.WholeWord; _regex.IsChecked = query.RegularExpression;
        _files.IsChecked = query.FilesOnly; _content.IsChecked = !query.FilesOnly;
        void Schedule() { if (_opening || _closed || _contextInvalid) return; Invalidate(); UpdateQuickFilters(); if (quick) UpdateQuickCategoryVisuals(); _status.Text = "等待搜索…"; SetQuickEmpty("正在搜索…"); _delay.Start(); }
        void ModeChanged() { options.IsVisible = !Query.FilesOnly; _query.PlaceholderText = quick ? "" : Query.FilesOnly ? "文件名或路径；支持空格分词及 :行号" : "搜索工作区代码…"; Schedule(); }
        _query.TextChanged += (_, _) => Schedule(); _mask.TextChanged += (_, _) => Schedule();
        _files.IsCheckedChanged += (_, _) => ModeChanged(); _content.IsCheckedChanged += (_, _) => ModeChanged();
        foreach (var option in new[] { _case, _word, _regex }) option.IsCheckedChanged += (_, _) => Schedule();
        _delay.Tick += async (_, _) => { _delay.Stop(); await SearchAsync(); };
        refresh.Click += (_, _) => Schedule();
        _results.SelectionChanged += (_, _) => { _open.IsEnabled = !_opening && _results.SelectedItem is CodeSearchMatch; UpdateQuickSelection(); };
        _open.Click += async (_, _) => await OpenSelectedAsync(); _results.DoubleTapped += async (_, _) => await OpenSelectedAsync();
        (quick ? (Control)Content! : this).AddHandler(KeyDownEvent, async (_, key) =>
        {
            if (new[] { _query, _mask }.Any(input => input.IsKeyboardFocusWithin && input.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText)))) return;
            if (quick && HandleQuickNavigation(key)) return;
            // IME confirmation stays in text input. The quick entry also accepts
            // an uncomposed Enter in the query; the detailed search keeps its
            // existing result-list-only Enter behavior.
            if (key.Key == Key.Escape) { key.Handled = true; CloseSearch(); }
            else if (key.Key == Key.F5) { key.Handled = true; Schedule(); }
            else if (key.Key == Key.Down && (_query.IsKeyboardFocusWithin || _mask.IsKeyboardFocusWithin) && _results.ItemCount > 0)
            { key.Handled = true; _results.SelectedIndex = Math.Max(0, _results.SelectedIndex); (_results.ContainerFromIndex(_results.SelectedIndex) ?? _results).Focus(); }
            else if (key.Key == Key.Enter && _results.IsKeyboardFocusWithin) { key.Handled = true; await OpenSelectedAsync(); }
            else if (quick && key.Key == Key.Enter && _query.IsKeyboardFocusWithin)
            {
                key.Handled = true;
                if (_resultSequence == _sequence) await OpenSelectedAsync();
                else { _pendingOpen = _sequence; if (_delay.IsEnabled) { _delay.Stop(); await SearchAsync(); } }
            }
        }, RoutingStrategies.Tunnel);
        _searchOpened = () => { ModeChanged(); _query.Focus(); _query.SelectAll(); };
        Opened += (_, _) => _searchOpened();
        Closing += (_, key) => { if (_opening) key.Cancel = true; };
        Closed += (_, _) => { _closed = true; Invalidate(); };
    }

    private void Invalidate()
    {
        CancelPreview();
        ++_sequence; _resultSequence = -1; _pendingOpen = null; _delay.Stop(); _read?.Cancel(); _read?.Dispose(); _read = null;
        _results.ItemsSource = null; _visibleMatches = null; _open.IsEnabled = false;
        _textHits.Clear();
    }
    private async Task SearchAsync()
    {
        if (_closed || _opening || _contextInvalid) return;
        if (IsActionSearch)
        {
            var commands = ShellCommands.SearchActions(_query.Text ?? "", OperatingSystem.IsMacOS(), _canRunAction);
            _results.ItemsSource = commands; _results.SelectedIndex = commands.Count > 0 ? 0 : -1;
            _resultSequence = _sequence;
            _status.Text = $"{commands.Count} available actions";
            SetQuickEmpty("No available actions match");
            if (_pendingOpen == _sequence) { _pendingOpen = null; await OpenSelectedAsync(); }
            return;
        }
        var query = Query; var owner = _sequence; _read = new(); var token = _read.Token;
        bool Current() => !_closed && !_closing && !_opening && !_contextInvalid && owner == _sequence && !token.IsCancellationRequested;
        async Task PublishFiles(CodeSearchPage files)
        {
            if (query.Text.Length == 0) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current()) return;
                ApplyMatches(files, owner);
                _status.Text = $"{files.Matches.Count:N0} 个文件结果" + (files.Truncated ? "（已到 1,000 条上限）" : "") +
                    " · 正在搜索代码内容…" + (_inventoryTruncated ? " · 文件列表已达 20,000 项，结果范围受限" : "");
                SetQuickEmpty(files.Matches.Count == 0 ? "正在搜索代码内容…" : "");
                if (_pendingOpen == owner && _results.SelectedItem is CodeSearchMatch)
                { _pendingOpen = null; _ = OpenSelectedAsync(); }
            });
        }
        _status.Text = query.Text.Length == 0 ? "输入搜索文本。↑ / ↓ 选择结果，Enter 打开，Esc 取消。" : "正在搜索工作区…";
        try
        {
            var page = _quick && _category == QuickCategory.All
                ? await RepositoryReader.SearchEverywhereAsync(_root, _paths(), query, _recentPaths, PublishFiles, token)
                : await RepositoryReader.SearchAsync(_root, _paths(), query, _recentPaths, token);
            if (!Current()) return;
            ApplyMatches(page, owner);
            SetQuickEmpty(query.Text.Length == 0 ? (query.FilesOnly ? "输入文件名或路径；可用 :行号定位" : "输入要查找的代码") : "没有匹配结果");
            if (query.Text.Length == 0) return;
            _status.Text = $"{page.Matches.Count:N0} 处结果" + (page.Truncated ? "（已到 1,000 条上限，请缩小范围）" : "") +
                (query.FilesOnly ? "" : $" · 搜索 {page.SearchedFiles:N0} 个文件" + (page.SkippedFiles > 0 ? $" · 跳过 {page.SkippedFiles:N0} 个受限或不可读文件" : "")) +
                (_inventoryTruncated ? " · 文件列表已达 20,000 项，结果范围受限" : "");
            if (_pendingOpen == owner) { _pendingOpen = null; await OpenSelectedAsync(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current()) { Invalidate(); _status.Text = ex.Message; SetQuickEmpty("无法搜索，请检查搜索条件"); } }
    }

    private void ApplyMatches(CodeSearchPage page, long owner)
    {
        if (_visibleMatches is null)
        {
            _visibleMatches = new(page.Matches); _results.ItemsSource = _visibleMatches;
        }
        else
        {
            // The final page keeps the exact file prefix. Append text hits without
            // replacing rows, selection, scroll position or the active preview.
            _visibleMatches.AppendPage(page.Matches);
        }
        if (_results.SelectedIndex < 0 && _visibleMatches.Count > 0) _results.SelectedIndex = 0;
        _resultSequence = owner;
    }

    public void InvalidateSearchContext()
    {
        if (_closed || _contextInvalid) return;
        _contextInvalid = true; Invalidate(); _query.IsEnabled = _mask.IsEnabled = false;
        _status.Text = "仓库已变化，请关闭后重新搜索。";
        SetQuickEmpty("仓库已变化，请关闭后重新搜索。");
    }
    private async Task OpenSelectedAsync()
    {
        if (_opening || _closed || _resultSequence != _sequence) return;
        if (_results.SelectedItem is ShellCommandDefinition command)
        {
            if (!_canRunAction(command.Id))
            {
                Invalidate(); await SearchAsync();
                _status.Text = "This action is no longer available.";
                return;
            }
            RequestedCommand = command.Id; CloseSearch(); return;
        }
        if (_results.SelectedItem is not CodeSearchMatch match) return;
        _opening = true; _delay.Stop(); _read?.Cancel(); _open.IsEnabled = false; _query.IsEnabled = _mask.IsEnabled = false;
        try
        {
            var error = await _openMatch(match);
            if (error is null) { OpenedMatch = true; _opening = false; CloseSearch(); }
            else _status.Text = error;
        }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally
        {
            _opening = false; _query.IsEnabled = _mask.IsEnabled = !_contextInvalid; _open.IsEnabled = !_contextInvalid && _results.SelectedItem is CodeSearchMatch;
            if (_quick && !_closed) _query.Focus();
        }
    }
}
