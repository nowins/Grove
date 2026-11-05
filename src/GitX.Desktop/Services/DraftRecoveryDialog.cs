using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;

namespace GitX.Desktop.Services;
internal static class DraftRecoveryDialog
{
    public static async Task<TextDraftRecord?> ShowAsync(Window owner, string? root, TextDraftStore store, bool enabled)
    {
        var window = new Window { Title = "本地草稿恢复", Width = 900, Height = 680, MinWidth = 580, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var list = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var left = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
        var right = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
        var toggle = new CheckBox { Content = "启用本机草稿检查点（默认关闭）", IsChecked = enabled };
        var restore = new Button { Content = "恢复到编辑器", IsEnabled = false };
        var discard = new Button { Content = "放弃选中草稿", IsEnabled = false };
        var clear = new Button { Content = "关闭持久化并清理所有仓库副本" };
        var close = new Button { Content = "关闭", IsCancel = true, IsDefault = true };
        IReadOnlyList<TextDraftRecord> records = []; long generation = 0;
        async Task Refresh()
        {
            var result = root is null ? new TextDraftListing([], 0, 0) : await Task.Run(() => store.List(root)); records = result.Drafts;
            list.ItemsSource = records.Select(r => $"{r.Path} · {r.Updated.ToLocalTime():g}").ToArray(); list.SelectedIndex = records.Count > 0 ? 0 : -1;
            status.Text = root is null ? "未打开仓库；可更改设置或清理副本。仓库移动或删除后不会把旧草稿归给其他仓库。" : $"{records.Count} 项可恢复；{result.Expired} 项超过 7 天，需手动清理；{result.Unreadable} 项损坏或不可读；{result.Unassigned} 项属于旧仓库身份（含旧 Linux 候选），原副本保留，不自动迁移。";
        }
        list.SelectionChanged += async (_, _) =>
        {
            var sequence = ++generation; restore.IsEnabled = discard.IsEnabled = false; left.Text = right.Text = "";
            if (root is null || list.SelectedIndex < 0 || list.SelectedIndex >= records.Count) return;
            var record = records[list.SelectedIndex]; right.Text = record.Text; discard.IsEnabled = true;
            try
            {
                var disk = await new RepositoryReader().DocumentAsync(new(root, record.Path, ComparisonKind.WorkingFile));
                if (sequence != generation) return;
                left.Text = disk.Text;
                restore.IsEnabled = !disk.IsNotice && disk.Editing?.CanEdit == true;
                status.Text = disk.IsNotice ? "文件已删除、移动或不可读。仅可复制恢复副本，不会重建原文件。" : disk.Editing?.Fingerprint == record.Baseline.Fingerprint ? "磁盘基准一致。恢复只改变编辑器；保存仍需明确操作。" : "磁盘、分支或权限已变化。恢复保留原基准，直接保存会被拒绝；请比较后手动合并。";
            }
            catch { if (sequence == generation) status.Text = "无法读取磁盘版本；恢复副本可选择复制，原文件未更改。"; }
        };
        restore.Click += (_, _) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < records.Count) window.Close(records[list.SelectedIndex]); };
        discard.Click += async (_, _) =>
        {
            if (root is null || list.SelectedIndex < 0 || list.SelectedIndex >= records.Count) return;
            try { store.Discard(root, records[list.SelectedIndex]); await Refresh(); }
            catch { status.Text = "副本已变化或无法清理，请重新打开窗口审阅。"; }
        };
        clear.Click += async (_, _) =>
        {
            // Two-step inline confirmation names the cross-repository scope.
            if (!Equals(clear.Tag, true)) { clear.Tag = true; clear.Content = "确认关闭并清理全部副本（不改原文件）"; return; }
            try { store.SetEnabled(false); toggle.IsChecked = false; store.Clear(); clear.Tag = false; clear.Content = "关闭持久化并清理所有仓库副本"; await Refresh(); }
            catch { status.Text = "部分副本无法清理；未删除任何原文件，请重新打开窗口检查。"; }
        };
        toggle.IsCheckedChanged += (_, _) =>
        {
            try { store.SetEnabled(toggle.IsChecked == true); status.Text = toggle.IsChecked == true ? "已启用。检查点约延迟 750 毫秒；断电前尚未写入的输入可能丢失。" : "已关闭后续持久化。已有副本不会自动删除，可用清理按钮移除。"; }
            catch { status.Text = "无法更改设置，未确认启用状态。"; }
        };
        close.Click += (_, _) => window.Close();
        var body = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 10 };
        body.Children.Add(toggle);
        var privacy = new TextBlock { Text = "仅本机明文，限当前用户目录；不上传。每份正文 ≤1 MiB，总记录 ≤16 MiB / 64 份；不自动驱逐。排除 .git、.ssh、.env*、*.pem/key/p12/pfx/keystore、id_rsa、id_ed25519、credentials(.json)、secrets.json；规则不保证识别所有秘密。", TextWrapping = TextWrapping.Wrap, Classes = { "dialog-hint" } }; Grid.SetRow(privacy, 1); body.Children.Add(privacy);
        Grid.SetRow(list, 2); body.Children.Add(list); Grid.SetRow(status, 3); body.Children.Add(status);
        var comparison = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 10 };
        var diskPanel = new DockPanel(); var diskLabel = new TextBlock { Text = "当前磁盘（只读对照）" }; DockPanel.SetDock(diskLabel, Dock.Top); diskPanel.Children.Add(diskLabel); diskPanel.Children.Add(left);
        var draftPanel = new DockPanel(); var draftLabel = new TextBlock { Text = "恢复副本（只读，可复制）" }; DockPanel.SetDock(draftLabel, Dock.Top); draftPanel.Children.Add(draftLabel); draftPanel.Children.Add(right);
        comparison.Children.Add(diskPanel); Grid.SetColumn(draftPanel, 1); comparison.Children.Add(draftPanel); Grid.SetRow(comparison, 4); body.Children.Add(comparison);
        Grid.SetRow(clear, 5); body.Children.Add(clear);
        window.Content = DialogChrome.Layout(body, DialogChrome.Buttons(close, discard, restore)); DialogChrome.Apply(window);
        await Refresh();
        return await window.ShowDialog<TextDraftRecord?>(owner);
    }
}
