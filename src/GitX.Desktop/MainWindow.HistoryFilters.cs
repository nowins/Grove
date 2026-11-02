using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private static IEnumerable<HistoryBranchNode> HistoryBranchDescendants(IEnumerable<HistoryBranchNode> nodes)
        => nodes.SelectMany(node => new[] { node }.Concat(HistoryBranchDescendants(node.Children)));

    private void ShowHistoryPopup(Control panel, Control anchor, Control? focus = null, bool form = false)
    {
        var owner = GetTopLevel(anchor);
        if (owner is not null) panel.Width = Math.Min(panel.Width, Math.Max(160, owner.Bounds.Width - 48));
        var flyout = new Flyout { OverlayDismissEventPassThrough = true, Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(160, (owner?.Bounds.Height ?? 600) - 64), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        flyout.FlyoutPresenterClasses.Add("shell-popup");
        if (form) flyout.FlyoutPresenterClasses.Add("history-filter");
        UiMotion.PreparePopup(panel);
        flyout.Opened += (_, _) =>
        {
            panel.Opacity = 1;
            focus?.Focus();
        };
        panel.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true; CloseShellPopup(restoreFocus: true);
        }, RoutingStrategies.Bubble);
        var right = owner is not null && anchor.TranslatePoint(default, owner) is { } point && point.X + panel.Width + 20 > owner.Bounds.Width;
        ShowShellPopup(flyout, anchor, right ? PlacementMode.BottomEdgeAlignedRight : PlacementMode.BottomEdgeAlignedLeft, restoreFocusOnDismiss: false);
    }

    private async void HistorySearchOptionClicked(object? sender, RoutedEventArgs e) => await _model.ApplyHistoryQueryAsync();

    private void HistoryBranchFilterClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || ToggleShellPopup(anchor)) return;
        var panel = new StackPanel { Width = 300, Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "分支与标签", Classes = { "dialog-label" }, Margin = new(4, 4) });
        var search = new TextBox { PlaceholderText = "分支或标签", Margin = new(4,4,4,6) };
        AutomationProperties.SetName(search, "筛选历史引用候选"); panel.Children.Add(search);
        var all = new[] { new HistoryBranchNode("all", "全部提交"), new HistoryBranchNode("HEAD", "HEAD（当前分支）") }
            .Concat(_model.HistoryReferenceCandidates.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new HistoryBranchNode(r.CanonicalName, r.Name, r))).ToArray();
        var list = new ListBox { MaxHeight = 260, MinHeight = 70, ItemsSource = all,
            ItemTemplate = HistoryBranchTree.ItemTemplate };
        AutomationProperties.SetName(list, "历史引用候选"); panel.Children.Add(list);
        var count = new TextBlock { Classes = { "filter-summary" }, Margin = new(4), Text = $"{all.Length} 个候选" }; panel.Children.Add(count);
        search.TextChanged += (_, _) =>
        {
            var matches = all.Where(n => n.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = matches; count.Text = matches.Length == 0 ? "没有匹配的分支或标签" : $"{matches.Length} 个候选";
        };
        list.SelectionChanged += async (_, _) =>
            { if (list.SelectedItem is HistoryBranchNode node) { CloseShellPopup(restoreFocus: true); await _model.SelectHistoryReferenceAsync(node.Key == "all" ? null : node.Key); } };
        search.KeyDown += (_, args) => { if (args.Key == Key.Down && list.ItemCount > 0) { args.Handled = true; list.Focus(); } };
        ShowHistoryPopup(panel, anchor, search);
    }

    private void HistoryValueFilter(Control anchor, string label, string value, string hint, IEnumerable<string> candidates, Action<string> apply)
    {
        if (ToggleShellPopup(anchor)) return;
        var panel = new StackPanel { Width = 320, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = label, Classes = { "dialog-label" } });
        panel.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(value) ? "当前未设置条件" : "当前：" + value, Classes = { "filter-summary" } });
        var input = new TextBox { Text = value, PlaceholderText = hint };
        AutomationProperties.SetName(input, label + "筛选值"); panel.Children.Add(input);
        var values = candidates.Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var list = new ListBox { MaxHeight = 190, MinHeight = 26, ItemsSource = values.Take(200).ToArray() };
        AutomationProperties.SetName(list, label + "筛选候选"); panel.Children.Add(list);
        var count = new TextBlock { Classes = { "filter-summary" } }; panel.Children.Add(count);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (IBrush)this.FindResource("WarningBrush")!, IsVisible = false };
        panel.Children.Add(error);
        async Task Apply(string text)
        {
            try { apply(text); CloseShellPopup(restoreFocus: true); await _model.ApplyHistoryQueryAsync(); }
            catch (IOException exception) { error.Text = exception.Message; error.IsVisible = true; }
        }
        void FilterCandidates()
        {
            var matches = values.Where(v => v.Contains(input.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = matches.Take(200).ToArray();
            count.Text = matches.Length == 0 ? "没有匹配候选，可直接输入并应用" : matches.Length > 200 ? $"显示前 200 个，共 {matches.Length} 个候选" : $"{matches.Length} 个候选";
        }
        input.TextChanged += (_, _) => FilterCandidates();
        FilterCandidates();
        list.SelectionChanged += async (_, _) => { if (list.SelectedItem is string selected) await Apply(selected); };
        var clear = new Button { Content = "清除", IsEnabled = !string.IsNullOrEmpty(value) }; var confirm = new Button { Content = "应用", Classes = { "primary" } };
        input.TextChanged += (_, _) => clear.IsEnabled = !string.IsNullOrEmpty(input.Text) || !string.IsNullOrEmpty(value);
        clear.Click += async (_, _) => await Apply(""); confirm.Click += async (_, _) => await Apply(input.Text ?? "");
        panel.Children.Add(new TextBlock { Text = hint, Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(DialogChrome.Footer(DialogChrome.Buttons(clear, confirm)));
        ShowHistoryPopup(panel, anchor, input, form: true);
    }

    private void ValidateHistoryDraft(string? author = null, string? from = null, string? until = null, string? path = null)
        => HistoryQueryInput.Create(_model.HistoryText, author ?? _model.HistoryAuthor, from ?? _model.HistoryFrom,
            until ?? _model.HistoryUntil, path ?? _model.HistoryPath, _model.HistoryMatchCase, _model.HistoryUseRegex);

    private void HistoryAuthorFilterClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        HistoryValueFilter(anchor, "作者", _model.HistoryAuthor, "姓名或邮箱 · 候选来自已加载提交", _model.Commits.Select(c => c.Author), value =>
        { ValidateHistoryDraft(author: value); _model.HistoryAuthor = value; });
    }
    private void HistoryPathFilterClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        var paths = _model.RepositoryPaths.SelectMany(path => new[] { path }.Concat(path.Select((c, i) => c == '/' ? path[..i] : null).OfType<string>()));
        HistoryValueFilter(anchor, "路径", _model.HistoryPath, "文件或目录 · Git 路径区分大小写，可输入历史路径", paths, value =>
        { ValidateHistoryDraft(path: value); _model.HistoryPath = value; });
    }
    private void HistoryDateFilterClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || ToggleShellPopup(anchor)) return;
        var panel = new StackPanel { Width = 290, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "日期范围", Classes = { "dialog-label" } });
        var error = new TextBlock { IsVisible = false, TextWrapping = TextWrapping.Wrap, Foreground = (IBrush)this.FindResource("WarningBrush")! };
        async Task Apply(string from, string until)
        {
            try { ValidateHistoryDraft(from: from, until: until); }
            catch (IOException exception) { error.Text = exception.Message; error.IsVisible = true; return; }
            _model.HistoryFrom = from; _model.HistoryUntil = until; CloseShellPopup(restoreFocus: true); await _model.ApplyHistoryQueryAsync();
        }
        var presets = new StackPanel();
        foreach (var (label, days) in new[] { ("不限日期", 0), ("今天", 1), ("最近 7 天", 7), ("最近 30 天", 30) })
            presets.Children.Add(ShellAction(label, null, async () => await Apply(days == 0 ? "" : DateTime.Today.AddDays(1 - days).ToString("yyyy-MM-dd"), days == 0 ? "" : DateTime.Today.ToString("yyyy-MM-dd"))));
        panel.Children.Add(presets);
        panel.Children.Add(new Separator());
        panel.Children.Add(new TextBlock { Text = "自定义作者日期（含起止当天）", Classes = { "dialog-label" } });
        var from = new TextBox { Text = _model.HistoryFrom, PlaceholderText = "开始日期 · yyyy-MM-dd" };
        var until = new TextBox { Text = _model.HistoryUntil, PlaceholderText = "结束日期 · yyyy-MM-dd" };
        AutomationProperties.SetName(from, "历史开始日期"); AutomationProperties.SetName(until, "历史结束日期");
        panel.Children.Add(from); panel.Children.Add(until);
        panel.Children.Add(error);
        var button = new Button { Content = "应用日期", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += async (_, _) =>
        { try { await Apply(from.Text ?? "", until.Text ?? ""); } catch (IOException exception) { error.Text = exception.Message; error.IsVisible = true; } };
        panel.Children.Add(DialogChrome.Footer(DialogChrome.Buttons(button))); ShowHistoryPopup(panel, anchor, from, form: true);
    }
    private void HistorySettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || ToggleShellPopup(anchor)) return;
        var panel = new StackPanel { Width = 220, Spacing = 2 };
        void Choice(string label, bool value, Action change)
            => panel.Children.Add(ShellAction(label, value ? "CheckIcon" : null, () => { change(); CloseShellPopup(); }));
        Choice("显示作者", _model.HistoryShowAuthor, () => _model.HistoryShowAuthor = !_model.HistoryShowAuthor);
        Choice("显示日期", _model.HistoryShowDate, () => _model.HistoryShowDate = !_model.HistoryShowDate);
        Choice("显示引用标签", _model.HistoryShowReferences, () => _model.HistoryShowReferences = !_model.HistoryShowReferences);
        panel.Children.Add(new Separator());
        Choice("显示分支树", _historyLeftVisible, () => HistoryBranchesToggled(null, new()));
        Choice("显示文件与详情", _historyRightVisible, () => HistoryFilesToggled(null, new()));
        ShowHistoryPopup(panel, anchor);
    }
}
