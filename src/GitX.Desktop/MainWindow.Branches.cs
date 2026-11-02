using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private sealed record BranchRow(BranchEntry? Branch, string Group);

    private async void BranchesClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.Root is not { } root || _model.IsWriting || ToggleShellPopup(BranchButton)) return;
        var generation = _shellGeneration;
        try
        {
            var context = await RepositoryActions.ReadAsync(root);
            if (_closed || _model.Root != root || generation != _shellGeneration || _model.IsWriting) return;
            var filter = new TextBox { PlaceholderText = "搜索分支名称…", Classes = { "search" } };
            AutomationProperties.SetName(filter, "搜索分支");
            var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new(0), MaxHeight = Math.Clamp(Bounds.Height - 260, 170, 410) };
            list.ItemTemplate = new FuncDataTemplate<BranchRow>((row, _) =>
            {
                if (row is null) return new Border();
                if (row.Branch is not { } branch) return new Border();
                var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("20,*,16"), Margin = new(0,2) };
                grid.Children.Add(IconContent.Icon(branch.Current ? "CheckIcon" : "BranchIcon"));
                var label = new StackPanel { Spacing = 2 };
                label.Children.Add(new TextBlock { Text = branch.Name + (branch.Current ? "  · 当前" : ""), TextTrimming = TextTrimming.CharacterEllipsis });
                if (branch.Upstream is { } upstream) label.Children.Add(new TextBlock { Text = upstream, FontSize = UiTypography.SecondarySize, Classes = { "muted" } });
                Grid.SetColumn(label, 1); grid.Children.Add(label);
                var arrow = IconContent.Icon("ChevronRightIcon"); Grid.SetColumn(arrow, 2); grid.Children.Add(arrow);
                ToolTip.SetTip(grid, branch.Name + "\n" + branch.Sha + (branch.Upstream is null ? "" : "\n跟踪 " + branch.Upstream));
                AutomationProperties.SetName(grid, branch.ToString());
                if (row.Group.Length == 0) return grid;
                return new StackPanel { Children = {
                    new Border { Background = (IBrush)this.FindResource("MenuBrush")!, Margin = new(-8,-2,-8,0), Padding = new(8,5),
                        Child = new TextBlock { Text = row.Group, Classes = { "muted" }, FontSize = UiTypography.SecondarySize } }, grid } };
            });
            var panel = new StackPanel { Width = Math.Min(360, Bounds.Width - 100), Spacing = 0 };
            var flyout = new Flyout { Content = panel }; flyout.FlyoutPresenterClasses.Add("shell-popup"); flyout.FlyoutPresenterClasses.Add("branch-popup");
            filter.Margin = new(4,4,4,6);
            var empty = new TextBlock { Text = "没有匹配的分支", Classes = { "muted" }, Margin = new(8) };
            BranchEntry? lastTarget = null;
            bool Valid() => !_closed && _model.Root == root && !_model.IsWriting;
            void Filter()
            {
                var matches = context.Branches.Where(b => !(b.Remote && b.CanonicalName.EndsWith("/HEAD", StringComparison.Ordinal)) &&
                    b.Name.Contains(filter.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
                var rows = new List<BranchRow>();
                foreach (var remote in new[] { false, true })
                {
                    var group = matches.Where(b => b.Remote == remote).OrderByDescending(b => b.Current).ThenBy(b => b.Name).ToArray();
                    if (group.Length == 0) continue;
                    rows.AddRange(group.Select((b, index) => new BranchRow(b, index == 0 ? remote ? "远程分支" : "本地分支" : "")));
                }
                list.ItemsSource = rows;
                list.SelectedItem = rows.FirstOrDefault(r => r.Branch == lastTarget) ?? rows.FirstOrDefault(r => r.Branch is not null);
                empty.IsVisible = matches.Length == 0;
            }
            filter.TextChanged += (_, _) => Filter();
            void ShowList()
            {
                panel.Children.Clear(); panel.Children.Add(filter);
                panel.Children.Add(ShellAction("新建分支…", "PlusIcon", () => _ = ExecuteBranchActionAsync(context, BranchAction.Create, null), BranchActionAvailability.DisabledReason(context, BranchAction.Create, null)));
                panel.Children.Add(new Separator()); panel.Children.Add(list); panel.Children.Add(empty);
                if (context.Dirty || context.InOperation)
                    panel.Children.Add(new TextBlock { Text = context.InOperation ? "当前有未完成的 Git 操作，可查看与比较分支。" : "有本地改动：切换、合并和变基前请先提交或 Stash。", TextWrapping = TextWrapping.Wrap, Foreground = (IBrush)this.FindResource("WarningBrush")!, Margin = new(8,4) });
                Filter(); filter.Focus();
            }
            void ShowActions(BranchEntry target)
            {
                if (!Valid()) { CloseShellPopup(); return; }
                lastTarget = target; panel.Children.Clear();
                panel.Children.Add(ShellAction("← 所有分支", null, ShowList));
                panel.Children.Add(new TextBlock { Text = target.Name, FontWeight = FontWeight.SemiBold, Margin = new(8,4), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = target.Sha[..12] + (target.Upstream is null ? "" : "  · " + target.Upstream), Classes = { "muted" }, Margin = new(8,0,8,6), TextTrimming = TextTrimming.CharacterEllipsis });
                void Action(string label, string icon, BranchAction action)
                {
                    var reason = BranchActionAvailability.DisabledReason(context, action, target);
                    panel.Children.Add(ShellAction(label, icon, () => _ = ExecuteBranchActionAsync(context, action, target), reason));
                    if (reason is not null && action == BranchAction.Switch) panel.Children.Add(new TextBlock { Text = reason, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new(8,0,8,4) });
                }
                var checkout = BranchActionAvailability.CheckoutTarget(context, target)!;
                Action(target.Current || checkout.Current ? "当前分支" : checkout != target ? "切换到本地分支 " + checkout.Name : target.Remote ? "跟踪并切换到 " + target.Name : "切换到 " + target.Name, "BranchIcon", BranchAction.Switch);
                Action("从此处新建分支…", "PlusIcon", BranchAction.Create);
                panel.Children.Add(new Separator());
                if (context.Head is not null && target.Sha != context.Head) panel.Children.Add(ShellAction("与当前分支比较", "HistoryIcon", () => _ = CompareBranchAsync(context, target, false)));
                panel.Children.Add(ShellAction("与工作区比较", "ChangesIcon", () => _ = CompareBranchAsync(context, target, true)));
                panel.Children.Add(new Separator());
                Action("合并到 " + context.Branch + "…", "MergeIcon", BranchAction.Merge);
                Action("将 " + context.Branch + " 变基到此处…", "RebaseIcon", BranchAction.Rebase);
                Action("删除已合并的本地分支…", "DeleteIcon", BranchAction.Delete);
                panel.Children.OfType<Button>().Skip(1).FirstOrDefault(b => b.IsEnabled)?.Focus(NavigationMethod.Directional);
            }
            void OpenSelected() { if (list.SelectedItem is BranchRow { Branch: { } target }) ShowActions(target); }
            filter.KeyDown += (_, key) =>
            {
                if (key.Key == Key.Down) { key.Handled = true; FocusShellList(list); }
            };
            list.AddHandler(KeyDownEvent, (_, key) => { if (key.Key is Key.Enter or Key.Right) { key.Handled = true; OpenSelected(); } }, RoutingStrategies.Tunnel);
            list.PointerReleased += (_, pointer) =>
            {
                if (pointer.InitialPressMouseButton != MouseButton.Left || (pointer.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is not BranchRow { Branch: { } target }) return;
                ShowActions(target);
            };
            panel.AddHandler(KeyDownEvent, (_, key) =>
            {
                if (panel.Children.Contains(filter)) return;
                if (key.Key == Key.Left)
                {
                    key.Handled = true; ShowList();
                    // Reattach and measure the list before bringing its selected row into view.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_shellPopup, flyout) && panel.Children.Contains(filter)) FocusShellList(list);
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
                    return;
                }
                if (key.Key is not (Key.Up or Key.Down or Key.Home or Key.End)) return;
                var buttons = panel.Children.OfType<Button>().Where(b => b.IsEnabled).ToList();
                if (buttons.Count == 0) return;
                var current = buttons.FindIndex(b => b.IsKeyboardFocusWithin);
                var next = key.Key switch { Key.Home => 0, Key.End => buttons.Count - 1,
                    Key.Up => (current + buttons.Count - 1) % buttons.Count, _ => (current + 1) % buttons.Count };
                key.Handled = true; buttons[next].Focus(NavigationMethod.Directional);
            }, RoutingStrategies.Tunnel);
            flyout.Opened += (_, _) => filter.Focus(); ShowList(); ShowShellPopup(flyout, BranchButton);
        }
        catch (Exception ex) { if (!_closed && generation == _shellGeneration) _model.ReportError(ex.Message); }
    }

    private async Task ExecuteBranchActionAsync(ActionContext context, BranchAction action, BranchEntry? target)
    {
        if (_closed || _model.Root != context.Root || _model.IsWriting) return;
        CloseShellPopup();
        var reason = BranchActionAvailability.DisabledReason(context, action, target);
        if (reason is not null) { _model.ReportError(reason); return; }
        if (action == BranchAction.Switch && target is not null) target = BranchActionAvailability.CheckoutTarget(context, target);
        var name = "";
        if (action == BranchAction.Create)
        {
            var value = await InputDialog.ShowAsync(this, "新建分支", $"从 {target?.Name ?? context.Branch} 创建分支。创建后仍保留在当前分支。", required: true, inputLabel: "分支名称", acceptText: "创建");
            if (value is null) return; name = value;
        }
        else if (target is null) return;
        else if (action is BranchAction.Delete or BranchAction.Merge or BranchAction.Rebase)
        {
            var description = action switch {
                BranchAction.Delete => $"删除本地分支 {target.Name}？Git 会拒绝删除未合并或正在其他 worktree 使用的分支。",
                BranchAction.Merge => $"把 {target.Name} ({target.Sha[..8]}) 合入 {context.Branch}。发生冲突时可使用独立合并窗口处理。",
                _ => $"把 {context.Branch} 重放到 {target.Name} ({target.Sha[..8]})。这会改写当前分支提交历史。" };
            if (!await ConfirmDialog.ShowAsync(this, "分支操作", description, "执行")) return;
        }
        // The writer rechecks the captured repository token after input/confirmation.
        await ReturnToNavigationAfterAsync(() => _model.ExecuteActionAsync(context.Root, () => new BranchWriter().ExecuteAsync(context, action, target, name)));
        if (_model.HasOperation) ShowConflictsClicked(null, new RoutedEventArgs());
    }

    private async Task CompareBranchAsync(ActionContext context, BranchEntry target, bool worktree)
    {
        if (_closed || _model.Root != context.Root) return;
        CloseShellPopup(); SwitchView(2);
        if (!_bottomOpen) return;
        var old = new CommitEntry(target.Sha, target.Name, "", DateTimeOffset.MinValue);
        var next = worktree || context.Head is null ? null : new CommitEntry(context.Head, context.Branch, "", DateTimeOffset.MinValue);
        await _model.CompareHistoryAsync(old, next); ShowHistoryComparisonFiles();
    }
}
