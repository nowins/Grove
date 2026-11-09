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

    private void BranchesClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.Root is not { } root || _model.IsWriting || ToggleShellPopup(BranchButton)) return;
        var generation = _shellGeneration;
        var repository = _model.RepositoryGeneration;
        var session = new BranchMenuSession(root);
        try
        {
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
            var status = new TextBlock { Text = "正在读取分支…", Classes = { "muted" }, Margin = new(8), TextWrapping = TextWrapping.Wrap };
            IReadOnlyList<BranchEntry> branches = [];
            var loaded = false;
            long page = 0;
            BranchEntry? lastTarget = null;
            Button retry = null!;
            retry = ShellAction("重试", "RefreshIcon", () => _ = ReadBranchesAsync());
            retry.IsVisible = false;
            bool Valid() => !_closed && _model.Root == root && repository == _model.RepositoryGeneration &&
                generation == _shellGeneration && ReferenceEquals(_shellPopup, flyout) && !_model.IsWriting;
            void Filter()
            {
                var matches = branches.Where(b => !(b.Remote && b.CanonicalName.EndsWith("/HEAD", StringComparison.Ordinal)) &&
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
                empty.IsVisible = loaded && matches.Length == 0;
            }
            filter.TextChanged += (_, _) => Filter();
            void ShowList()
            {
                ++page; session.CancelActions();
                panel.Children.Clear(); panel.Children.Add(filter);
                panel.Children.Add(ShellAction("新建分支…", "PlusIcon", () => _ = LoadActionsAsync(null)));
                panel.Children.Add(new Separator()); panel.Children.Add(list); panel.Children.Add(empty); panel.Children.Add(status); panel.Children.Add(retry);
                Filter(); filter.Focus();
            }
            async Task ReadBranchesAsync()
            {
                status.Text = "正在读取分支…"; status.IsVisible = true; retry.IsVisible = false;
                try
                {
                    var result = await session.ReadAsync();
                    if (!Valid()) return;
                    branches = result; loaded = true; status.IsVisible = false; Filter();
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    if (!Valid()) return;
                    status.Text = "分支读取失败：" + error.Message;
                    retry.IsVisible = true;
                }
            }
            async Task LoadActionsAsync(BranchEntry? target)
            {
                if (!Valid()) return;
                var request = ++page;
                lastTarget = target; panel.Children.Clear();
                panel.Children.Add(ShellAction("← 所有分支", null, ShowList));
                panel.Children.Add(new TextBlock { Text = target?.Name ?? "新建分支", FontWeight = FontWeight.SemiBold, Margin = new(8,4), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = "正在检查工作区状态…", Classes = { "muted" }, Margin = new(8) });
                try
                {
                    var result = await session.ReadActionsAsync(target);
                    if (!Valid() || request != page) return;
                    if (target is null) { await ExecuteBranchActionAsync(result.Context, BranchAction.Create, null); return; }
                    ShowActions(target, result.Context, result.Worktrees);
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    if (!Valid() || request != page) return;
                    panel.Children.RemoveAt(panel.Children.Count - 1);
                    panel.Children.Add(new TextBlock { Text = error.Message, Classes = { "muted" }, Margin = new(8), TextWrapping = TextWrapping.Wrap });
                    panel.Children.Add(ShellAction("重试", "RefreshIcon", () => _ = LoadActionsAsync(target)));
                }
            }
            void ShowActions(BranchEntry target, ActionContext context, WorktreeSnapshot worktrees)
            {
                if (!Valid()) { CloseShellPopup(); return; }
                lastTarget = target; panel.Children.Clear();
                panel.Children.Add(ShellAction("← 所有分支", null, ShowList));
                panel.Children.Add(new TextBlock { Text = target.Name, FontWeight = FontWeight.SemiBold, Margin = new(8,4), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = target.Sha[..12] + (target.Upstream is null ? "" : "  · " + target.Upstream), Classes = { "muted" }, Margin = new(8,0,8,6), TextTrimming = TextTrimming.CharacterEllipsis });
                void Action(string label, string icon, BranchAction action)
                {
                    var reason = BranchActionAvailability.DisabledReason(context, action, target, worktrees.Entries);
                    panel.Children.Add(ShellAction(label, icon, () => _ = ExecuteBranchActionAsync(context, action, target), reason));
                    if (reason is not null && action == BranchAction.Switch) panel.Children.Add(new TextBlock { Text = reason, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new(8,0,8,4) });
                }
                var checkout = BranchActionAvailability.CheckoutTarget(context, target)!;
                Action(target.Current || checkout.Current ? "当前分支" : target.Remote ? "Checkout" : "切换到 " + target.Name, "BranchIcon", BranchAction.Switch);
                if (target.Remote && BranchCheckout.Resolve(context, target) is { Error: null } plan)
                    panel.Children.Add(new TextBlock { Text = plan.CreateTracking ? $"创建本地 {plan.LocalName}，跟踪 {target.Name}。" : $"使用本地 {plan.LocalName}，保留其提交与跟踪关系。", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new(8,0,8,4) });
                Action(target.Remote ? "New Branch…" : "从此处新建分支…", "PlusIcon", BranchAction.Create);
                panel.Children.Add(new Separator());
                if (context.Head is not null && target.Sha != context.Head) panel.Children.Add(ShellAction("与当前分支比较", "HistoryIcon", () => _ = CompareBranchAsync(context, target, false)));
                panel.Children.Add(ShellAction("与工作区比较", "ChangesIcon", () => _ = CompareBranchAsync(context, target, true)));
                panel.Children.Add(new Separator());
                Action("合并到 " + context.Branch + "…", "MergeIcon", BranchAction.Merge);
                Action("将 " + context.Branch + " 变基到此处…", "RebaseIcon", BranchAction.Rebase);
                Action("删除已合并的本地分支…", "DeleteIcon", BranchAction.Delete);
                panel.Children.OfType<Button>().Skip(1).FirstOrDefault(b => b.IsEnabled)?.Focus(NavigationMethod.Directional);
            }
            void OpenSelected() { if (list.SelectedItem is BranchRow { Branch: { } target }) _ = LoadActionsAsync(target); }
            filter.KeyDown += (_, key) =>
            {
                if (key.Key == Key.Down) { key.Handled = true; FocusShellList(list); }
            };
            list.AddHandler(KeyDownEvent, (_, key) => { if (key.Key is Key.Enter or Key.Right) { key.Handled = true; OpenSelected(); } }, RoutingStrategies.Tunnel);
            list.PointerReleased += (_, pointer) =>
            {
                if (pointer.InitialPressMouseButton != MouseButton.Left || (pointer.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is not BranchRow { Branch: { } target }) return;
                _ = LoadActionsAsync(target);
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
            flyout.Closed += (_, _) => session.Dispose();
            flyout.Opened += (_, _) => filter.Focus(); ShowList(); ShowShellPopup(flyout, BranchButton);
            _ = ReadBranchesAsync();
        }
        catch (Exception ex) { session.Dispose(); if (!_closed && generation == _shellGeneration) _model.ReportError(ex.Message); }
    }

    private async Task ExecuteBranchActionAsync(ActionContext context, BranchAction action, BranchEntry? target)
    {
        if (_closed || _model.Root != context.Root || _model.IsWriting) return;
        var repository = _model.RepositoryGeneration;
        CloseShellPopup();
        var reason = BranchActionAvailability.DisabledReason(context, action, target);
        if (reason is not null) { _model.ReportError(reason); return; }
        var name = "";
        if (action == BranchAction.Create)
        {
            var choice = await BranchCreateDialog.ShowAsync(this, context, target);
            if (choice is null) return;
            name = choice.Name; action = choice.Checkout ? BranchAction.CreateAndSwitch : BranchAction.Create;
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
        if (_closed || _model.Root != context.Root || repository != _model.RepositoryGeneration || _model.IsWriting) return;
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
