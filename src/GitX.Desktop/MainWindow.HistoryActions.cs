using Avalonia;
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
        if (_model.Root is not { } root || !_bottomOpen) return;
        var selected = CommitList.SelectedItems?.Cast<CommitEntry>().OrderBy(c => _model.Commits.ToList().IndexOf(c)).ToArray() ?? [];
        if (selected.Length == 0) return;
        var generation = ++_historyMenuGeneration;
        try
        {
            var context = await RepositoryActions.ReadAsync(root);
            if (_closed || _model.Root != root || !_bottomOpen || generation != _historyMenuGeneration ||
                !selected.Select(c => c.Sha).ToHashSet().SetEquals(CommitList.SelectedItems?.Cast<CommitEntry>().Select(c => c.Sha) ?? [])) return;
            var menu = new MenuFlyout();
            MenuItem Item(string label, Func<Task> action, bool enabled = true, string? reason = null, string? icon = null)
            {
                var item = new MenuItem { Header = label, IsEnabled = enabled, MinWidth = 220 };
                if (icon is not null) item.Icon = IconContent.Icon(icon);
                if (reason is not null) ToolTip.SetTip(item, reason);
                item.Click += async (_, _) =>
                {
                    menu.Hide();
                    if (_closed || _model.Root != root || !_bottomOpen) return;
                    try { await action(); } catch (Exception ex) { _model.ReportError(ex.Message); }
                };
                menu.Items.Add(item); return item;
            }
            Item("复制 SHA", async () =>
            {
                var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。");
                await clipboard.SetTextAsync(string.Join('\n', selected.Select(c => c.Sha)));
            }).InputGesture = new KeyGesture(Key.C, KeyModifiers.Control);
            Item("比较两个提交", async () => { await _model.CompareHistoryAsync(selected[1], selected[0]); ShowHistoryComparisonFiles(); }, selected.Length == 2, "选择两个提交后比较版本");
            Item("与本地比较", async () => { await _model.CompareHistoryAsync(selected[0], null); ShowHistoryComparisonFiles(); }, selected.Length == 1);
            menu.Items.Add(new Separator());
            string? UnavailableReason(bool needsClean) => selected.Length != 1 ? "此操作需要选择一个提交" : _model.IsWriting ? "Git 操作正在执行" : context.InOperation ? "请先完成或中止当前 Git 操作" : context.Head is null ? "当前分支尚无提交" : needsClean && context.Dirty ? "请先提交改动或使用 Stash 保存" : null;
            foreach (var (label, action) in new[] { ("Cherry-pick…", HistoryAction.CherryPick), ("Revert…", HistoryAction.Revert) })
            {
                var reason = UnavailableReason(needsClean: true);
                Item(label, () => RunHistoryActionAsync(context, selected[0], action, label), reason is null, reason, action == HistoryAction.CherryPick ? "CherryPickIcon" : "UndoIcon");
            }
            var resetReason = UnavailableReason(needsClean: false);
            Item(context.Detached ? "Reset 分离 HEAD 到此处…" : "Reset 当前分支到此处…", () => RunResetHistoryActionAsync(context, selected[0]), resetReason is null, resetReason, "UndoIcon");
            menu.ShowAt(anchor, atPointer);
        }
        catch (Exception ex) { if (!_closed && _model.Root == root) _model.ReportError(ex.Message); }
    }

    private async Task CopyHistoryShasAsync()
    {
        var selected = CommitList.SelectedItems?.OfType<CommitEntry>().ToArray() ?? [];
        if (selected.Length == 0) return;
        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。");
        await clipboard.SetTextAsync(string.Join('\n', selected.Select(commit => commit.Sha)));
    }

    private async Task RunHistoryActionAsync(ActionContext context, CommitEntry commit, HistoryAction action, string label)
    {
        var mainline = 0;
        if (action is HistoryAction.CherryPick or HistoryAction.Revert && commit.Parents?.Count > 1)
        {
            var value = await InputDialog.ShowAsync(this, "选择主线父提交", string.Join('\n', commit.Parents.Select((p, i) => $"{i + 1}: {p}")) + "\n输入主线父提交序号。", "1");
            if (value is null) return;
            if (!int.TryParse(value, out mainline) || mainline < 1 || mainline > commit.Parents.Count) { _model.ReportError("主线父提交序号无效。"); return; }
        }
        var description = action switch
        {
            HistoryAction.CherryPick => "把此提交的改动应用到当前分支并创建提交。",
            HistoryAction.Revert => "创建反向提交，保留原有历史。",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (!await ConfirmDialog.ShowAsync(this, label, $"{context.Branch} → {commit.ShortSha}\n{commit.Subject}\n\n{description}", "执行")) return;
        await _model.ExecuteActionAsync(context.Root, () => new HistoryWriter().ExecuteAsync(context, action, commit.Sha, mainline));
        if (_model.HasOperation) ShowConflictsClicked(null, new RoutedEventArgs());
    }

    private async Task RunResetHistoryActionAsync(ActionContext context, CommitEntry commit)
    {
        var action = await ResetDialog.ShowAsync(this, context, commit);
        if (action is null || _closed || _model.Root != context.Root || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => new HistoryWriter().ExecuteAsync(context, action.Value, commit.Sha), "Reset");
    }
}
