using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private long _historyMenuGeneration;
    private Action? _closeHistoryMenu;

    private async void HistoryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(CommitList).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not CommitEntry commit) return;
        e.Handled = true;
        if (CommitList.SelectedItems?.Contains(commit) != true) CommitList.SelectedItem = commit;
        // Handling the pointer event bypasses ListBoxItem's normal focus transfer.
        // Make the clicked row the return target for the menu and any modal action.
        row.Focus(NavigationMethod.Pointer);
        await ShowHistoryMenuAsync(row, atPointer: true);
    }

    private async void HistoryMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && _bottomOpen)
        {
            e.Handled = true;
            try { await CopyHistoryShasAsync(); } catch (Exception ex) { _model.ReportError(ex.Message); }
            return;
        }
        if (e.Key != Key.Apps && !(e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)) return;
        e.Handled = true;
        await ShowHistoryMenuAsync(CommitList.ContainerFromIndex(CommitList.SelectedIndex) ?? CommitList, atPointer: false);
    }

    private async Task ShowHistoryMenuAsync(Control anchor, bool atPointer)
    {
        if (_model.CaptureHistoryMenuState() is not { } state || !_bottomOpen) return;
        var root = state.Root;
        var order = _model.Commits.Select((commit, index) => (commit.Sha, index)).ToDictionary(pair => pair.Sha, pair => pair.index);
        var selected = CommitList.SelectedItems?.Cast<CommitEntry>().OrderBy(c => order.GetValueOrDefault(c.Sha, int.MaxValue)).ToArray() ?? [];
        if (selected.Length == 0) return;
        _closeHistoryMenu?.Invoke();
        var generation = ++_historyMenuGeneration;
        var repository = _model.RepositoryGeneration;
        var history = _model.HistoryGeneration;
        bool Current() => !_closed && _model.Root == root && _bottomOpen && generation == _historyMenuGeneration &&
            repository == _model.RepositoryGeneration && history == _model.HistoryGeneration &&
            selected.Select(c => c.Sha).ToHashSet().SetEquals(CommitList.SelectedItems?.Cast<CommitEntry>().Select(c => c.Sha) ?? []);
        var session = new HistoryMenuSession(state);
        var menu = new MenuFlyout();
        var claimed = false; var cleaned = false;
        void Cleanup()
        {
            if (cleaned) return;
            cleaned = true; session.Dispose(); _model.PropertyChanged -= Changed;
            if (generation == _historyMenuGeneration) _closeHistoryMenu = null;
        }
        void Close() { menu.Hide(); Cleanup(); }
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (!Current()) Close(); }
        _closeHistoryMenu = Close;
        _model.PropertyChanged += Changed;
        menu.Closed += (_, _) => { session.ClosePopup(); if (!claimed) Cleanup(); };
        async Task WithContext(Func<ActionContext, Task> action, bool clean = false, bool requireHead = false, bool local = false)
        {
            _model.ReportStatus("正在检查 Git 操作条件…");
            var context = await session.ReadContextAsync();
            if (!Current() || _model.IsWriting) return;
            if (context.InOperation) throw new IOException("请先完成或中止当前 Git 操作。");
            if (requireHead && context.Head is null) throw new IOException("当前分支尚无提交。");
            if (local && context.Detached) throw new IOException("请检出有提交的本地分支。");
            if (clean && context.Dirty) throw new IOException("请先提交改动或使用 Stash 保存。");
            await action(context);
        }
        try
        {
            MenuItem Item(string label, Func<Task> action, bool enabled = true, string? reason = null, string? icon = null, MenuItem? parent = null)
            {
                var item = new MenuItem { Header = new TextBlock { Text = label, MaxWidth = 330, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis }, IsEnabled = enabled, MinWidth = 340 };
                AutomationProperties.SetName(item, label);
                if (icon is not null) item.Icon = IconContent.Icon(icon);
                ToolTip.SetTip(item, reason ?? label);
                item.Click += async (_, _) =>
                {
                    claimed = true; menu.Hide();
                    try { if (Current()) await action(); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { if (!_closed && repository == _model.RepositoryGeneration) _model.ReportError(ex.Message); }
                    finally { Cleanup(); }
                };
                if (parent is null) menu.Items.Add(item); else parent.Items.Add(item);
                return item;
            }
            Item("复制 SHA", async () =>
            {
                var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。");
                await clipboard.SetTextAsync(string.Join('\n', selected.Select(c => c.Sha)));
            }).InputGesture = new KeyGesture(Key.C, KeyModifiers.Control);
            var single = selected.Length == 1;
            const string selectOne = "此操作需要选择一个提交";
            Item("复制提交说明", () => CopyHistoryMessageAsync(root, selected[0], Current), single, single ? null : selectOne);
            Item("创建提交补丁…", () => ExportHistoryPatchAsync(root, selected[0], Current), single, single ? "导出整个提交的文件差异；合并提交可选择父提交" : selectOne, "SaveIcon");
            menu.Items.Add(new Separator());
            Item("比较两个提交", async () => { await _model.CompareHistoryAsync(selected[1], selected[0]); ShowHistoryComparisonFiles(); }, selected.Length == 2, "选择两个提交后比较版本");
            Item("与本地比较", async () => { await _model.CompareHistoryAsync(selected[0], null); ShowHistoryComparisonFiles(); }, single);
            Item("浏览此修订的仓库", () => OpenRevisionBrowserAsync(root, selected[0], Current), single, single ? "只读浏览完整历史目录，不执行检出" : selectOne, "FolderIcon");
            var parents = selected[0].Parents?.Count ?? 0;
            Item(parents > 1 ? "转到父提交…" : "转到父提交", () => NavigateHistoryParentAsync(root, selected[0], Current), single && parents > 0,
                !single ? selectOne : parents == 0 ? "此提交没有可用的父提交" : "目标不在已加载列表时，清除历史筛选后定位", "UpIcon");
            Item("转到子提交…", () => NavigateHistoryChildAsync(root, selected[0], Current), single,
                single ? "查找实际父子关系；有多个子提交时选择目标" : selectOne, "DownIcon");
            menu.Items.Add(new Separator());
            string? ReferenceReason(bool clean) => !single ? selectOne : _model.IsWriting ? "Git 操作正在执行" : state.InOperation ? "请先完成或中止当前 Git 操作" :
                clean && state.Dirty ? "请先提交改动或使用 Stash 保存" : null;
            var referenceReason = ReferenceReason(false);
            Item("从此提交新建分支…", () => WithContext(context => CreateHistoryBranchAsync(context, selected[0], Current)), referenceReason is null, referenceReason, "BranchIcon");
            Item("从此提交新建标签…", () => WithContext(context => CreateHistoryTagAsync(context, selected[0], Current)), referenceReason is null, referenceReason);
            var checkoutReason = ReferenceReason(true);
            Item("检出此修订（分离 HEAD）…", () => WithContext(context => CheckoutHistoryRevisionAsync(context, selected[0], Current), clean: true), checkoutReason is null, checkoutReason, "BranchIcon");
            var branchMenu = new MenuItem { Header = single ? "此提交上的分支（读取中…）" : "此提交上的分支", MinWidth = 340, IsEnabled = false };
            ToolTip.SetTip(branchMenu, single ? "操作直接指向此提交的本地或远端分支" : selectOne); menu.Items.Add(branchMenu);
            var rebaseOntoReason = ReferenceReason(true) ?? (state.Head is null ? "当前工作树尚无提交" : state.Head == selected[0].Sha ? "此提交已是当前 HEAD" : null);
            Item("将当前 HEAD 变基到此提交…", () => WithContext(context => RebaseOntoHistoryAsync(context, selected[0], Current), clean: true, requireHead: true), rebaseOntoReason is null, rebaseOntoReason, "RebaseIcon");
            menu.Items.Add(new Separator());
            string? UnavailableReason(bool needsClean) => selected.Length != 1 ? "此操作需要选择一个提交" : _model.IsWriting ? "Git 操作正在执行" : state.InOperation ? "请先完成或中止当前 Git 操作" : state.Head is null ? "当前分支尚无提交" : needsClean && state.Dirty ? "请先提交改动或使用 Stash 保存" : null;
            foreach (var (label, action) in new[] { ("Cherry-pick…", HistoryAction.CherryPick), ("Revert…", HistoryAction.Revert) })
            {
                var reason = selected.Length > 100 ? "一次最多选择 100 个提交" : _model.IsWriting ? "Git 操作正在执行" : state.InOperation ? "请先完成或中止当前 Git 操作" :
                    state.Head is null ? "当前分支尚无提交" : state.Dirty ? "请先提交改动或使用 Stash 保存" : null;
                var caption = single ? label : label.Replace("…", $" {selected.Length} 个提交…");
                Item(caption, () => WithContext(context => RunHistoryBatchAsync(context, selected, action, caption, Current), clean: true, requireHead: true), reason is null, reason, action == HistoryAction.CherryPick ? "CherryPickIcon" : "UndoIcon");
            }
            var resetReason = UnavailableReason(needsClean: false);
            Item(state.Detached ? "Reset 分离 HEAD 到此处…" : "Reset 当前分支到此处…", () => WithContext(context => RunResetHistoryActionAsync(context, selected[0], Current), requireHead: true), resetReason is null, resetReason, "UndoIcon");
            menu.Items.Add(new Separator());
            string? RewriteReason(bool clean, bool one = false) => one && !single ? selectOne : selected.Length > 100 ? "一次最多整理 100 个提交" :
                _model.IsWriting ? "Git 操作正在执行" : state.InOperation ? "请先完成当前 Git 操作" : state.Head is null || state.Detached ? "请检出有提交的本地分支" : clean && state.Dirty ? "请先提交改动或使用 Stash 保存" : null;
            var rewordReason = RewriteReason(selected[0].Sha != state.Head, one: true);
            Item("修改提交说明…", () => WithContext(context => RewordHistoryAsync(context, selected[0], Current), clean: selected[0].Sha != state.Head, requireHead: true, local: true), rewordReason is null, rewordReason);
            var undoReason = RewriteReason(false, one: true) ?? (selected[0].Sha != state.Head ? "Undo Commit 仅适用于 HEAD" : parents != 1 ? "根提交／合并提交请使用明确的 Reset 操作" : null);
            Item("撤回当前提交（Undo Commit）…", () => WithContext(context => UndoHistoryAsync(context, selected[0], Current), requireHead: true, local: true), undoReason is null, undoReason, "UndoIcon");
            foreach (var (action, caption) in new[] { (RewriteAction.Squash, single ? "与上一提交合并（Squash）…" : $"合并 {selected.Length} 个提交（Squash）…"),
                (RewriteAction.Fixup, single ? "合入上一提交（Fixup）…" : $"合并 {selected.Length} 个提交（Fixup）…"),
                (RewriteAction.Drop, single ? "丢弃此提交（Drop）…" : $"丢弃 {selected.Length} 个提交（Drop）…") })
            {
                var reason = RewriteReason(true) ?? (single && parents != 1 && action is RewriteAction.Squash or RewriteAction.Fixup ? "此提交没有唯一的上一提交可供合并" : null);
                Item(caption, () => WithContext(context => RewriteHistorySelectionAsync(context, selected, action, caption, Current), clean: true, requireHead: true, local: true), reason is null, reason);
            }
            var interactiveReason = RewriteReason(true, one: true);
            Item("交互式 Rebase（从此提交起）…", () => WithContext(context => InteractiveHistoryRewriteAsync(context, selected[0], Current), clean: true, requireHead: true, local: true), interactiveReason is null, interactiveReason);
            menu.ShowAt(anchor, atPointer);
            if (!single) return;
            try
            {
                var catalog = await session.ReadBranchesAsync();
                if (!Current() || !menu.IsOpen) return;
                var branches = catalog.Where(b => b.Sha == selected[0].Sha && !(b.Remote && b.CanonicalName.EndsWith("/HEAD", StringComparison.Ordinal)))
                .OrderBy(b => b.Remote).ThenByDescending(b => b.Current).ThenBy(b => b.Name, StringComparer.Ordinal).ToArray();
                branchMenu.Header = $"此提交上的分支（{branches.Length}）";
                branchMenu.Items.Clear(); branchMenu.IsEnabled = branches.Length > 0;
                foreach (var branch in branches.Take(50))
                {
                    var caption = (branch.Remote ? "远端 · " : "本地 · ") + branch.Name + (branch.Current ? " · 当前" : "");
                    var group = new MenuItem { Header = new TextBlock { Text = caption, MaxWidth = 320, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis } };
                    AutomationProperties.SetName(group, caption);
                    ToolTip.SetTip(group, branch.CanonicalName); branchMenu.Items.Add(group);
                    Item("复制分支名称", async () =>
                    {
                        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。"); await clipboard.SetTextAsync(branch.Name);
                    }, parent: group);
                    Item("只显示此分支历史", () => _model.SelectHistoryReferenceAsync(branch.CanonicalName), parent: group);
                    Item("与当前 HEAD 比较", () => CompareBranchAsync(state.Root, state.Head, state.Branch, branch, false), state.Head is not null && state.Head != branch.Sha, parent: group);
                    Item("与工作区比较", () => CompareBranchAsync(state.Root, state.Head, state.Branch, branch, true), parent: group);
                    group.Items.Add(new Separator());
                    foreach (var (label, action) in new[] { (branch.Remote ? "Checkout" : "切换到此分支", BranchAction.Switch),
                    ("从此分支新建分支…", BranchAction.Create), ("重命名本地分支…", BranchAction.Rename),
                    ("合并到 " + state.Branch + "…", BranchAction.Merge), ("将 " + state.Branch + " 变基到此分支…", BranchAction.Rebase), ("删除已合并的本地分支…", BranchAction.Delete) })
                    {
                        var reason = _model.IsWriting ? "Git 操作正在执行" : BranchActionAvailability.DisabledReason(state.Head, state.Dirty, state.InOperation, catalog, action, branch);
                        Item(label, () => WithContext(context =>
                        {
                            if (!context.Branches.Contains(branch)) throw new IOException("分支已变化，请重新打开提交菜单。");
                            return ExecuteBranchActionAsync(context, action, branch, Current);
                        }), reason is null, reason, parent: group);
                    }
                }
                if (branches.Length > 50) Item($"前 50 个／共 {branches.Length} 个；查看全部分支…", () => { BranchesClicked(null, new RoutedEventArgs()); return Task.CompletedTask; }, parent: branchMenu);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (Current() && menu.IsOpen)
                {
                    branchMenu.Header = "分支读取失败；重新打开菜单重试";
                    ToolTip.SetTip(branchMenu, ex.Message);
                }
            }
        }
        catch (Exception ex) { Close(); if (!_closed && _model.Root == root) _model.ReportError(ex.Message); }
    }

    private async Task CopyHistoryShasAsync()
    {
        var selected = CommitList.SelectedItems?.OfType<CommitEntry>().ToArray() ?? [];
        if (selected.Length == 0) return;
        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。");
        await clipboard.SetTextAsync(string.Join('\n', selected.Select(commit => commit.Sha)));
    }

    private async Task RunResetHistoryActionAsync(ActionContext context, CommitEntry commit, Func<bool> current)
    {
        var action = await ResetDialog.ShowAsync(this, context, commit);
        if (action is null || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => new HistoryWriter().ExecuteAsync(context, action.Value, commit.Sha), "Reset");
    }
}
