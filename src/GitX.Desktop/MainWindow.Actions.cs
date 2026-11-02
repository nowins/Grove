using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private async void StashClicked(object? sender, RoutedEventArgs e) => await OpenStashesAsync();
    private async void BlameClicked(object? sender, RoutedEventArgs e) => await _model.ToggleBlameAsync();
    private async void AmendClicked(object? sender, RoutedEventArgs e) => await AmendAsync();
    private async void HistoryActionsClicked(object? sender, RoutedEventArgs e)
        => await ShowHistoryMenuAsync(sender as Control ?? CommitList, atPointer: false);

    private void ShowOutput()
    {
        var dialog = new Window { Title = "Git 操作输出", Width = 720, Height = 420, MinWidth = 500, MinHeight = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new TextBox { Text = _model.LastOperationOutput, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "code" }, Margin = new Thickness(16), PlaceholderText = "暂无 Git 操作输出。" };
        DialogChrome.Apply(dialog);
        dialog.ShowDialog(this);
    }

    private async Task AmendAsync()
    {
        if (_model.Root is not { } root || _model.IsWriting) return;
        try
        {
            var context = await RepositoryActions.ReadAsync(root);
            if (_model.Root != root || _closed) return;
            using var reader = new RepositoryReader();
            var snapshot = await reader.OpenAsync(root);
            var paths = snapshot.Changes.Where(change => change.Kind == ComparisonKind.Index).Select(change => change.Path).ToArray();
            var scope = paths.Length == 0 ? "索引无新增改动，仅修改上次提交说明。" : $"索引内 {paths.Length} 个文件：\n" + string.Join('\n', paths.Take(12)) + (paths.Length > 12 ? "\n…" : "");
            if (_model.Root != root || _closed) return;
            var message = await InputDialog.ShowAsync(this, "修改上次提交（Amend）", $"将改写 {context.Head?[..8]}，采用当前索引树。左侧包含勾选不参与 Amend；未暂存改动保留。若已推送，需协调远程历史。\n{scope}\n填写完整说明；点击确定后执行。", context.Message, multiline: true);
            if (message is null) return;
            await _model.ExecuteActionAsync(root, () => new HistoryWriter().AmendAsync(context, message));
            if (_model.HasError) _model.CommitMessage = message;
        }
        catch (Exception ex) { _model.ReportError(ex.Message); }
    }

    private async Task OpenStashesAsync()
    {
        if (_model.Root is not { } root || _model.IsWriting) return;
        try
        {
            var context = await RepositoryActions.ReadAsync(root);
            if (_closed || _model.Root != root) return;
            var dialog = new Window { Title = "Stash · " + context.Branch, Width = 600, Height = 390, MinWidth = 520, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var list = new ListBox { ItemsSource = context.Stashes, SelectedIndex = context.Stashes.Count > 0 ? 0 : -1 };
            var message = new TextBox { PlaceholderText = "保存说明（可选）" };
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, action) in new[] { ("保存本地改动", StashAction.Save), ("应用", StashAction.Apply), ("应用并移除", StashAction.Pop), ("删除…", StashAction.Drop) })
            {
                var icon = action switch { StashAction.Save => "StashIcon", StashAction.Drop => "DeleteIcon", StashAction.Pop => "UpIcon", _ => "CheckIcon" };
                var button = new Button { Content = IconContent.Label(icon, label), Classes = { "dialog-action" }, Margin = new Thickness(0,0,8,0), IsEnabled = action == StashAction.Save ? context.Dirty : context.Stashes.Count > 0 };
                if (action == StashAction.Save) button.Classes.Add("primary");
                Avalonia.Automation.AutomationProperties.SetName(button, label);
                button.Click += async (_, _) =>
                {
                    var stash = list.SelectedItem as StashEntry;
                    if (action != StashAction.Save && stash is null) return;
                    if (action == StashAction.Drop && !await ConfirmDialog.ShowAsync(dialog, "删除 Stash", $"删除 {stash}？这会移除该条保存记录。", "删除")) return;
                    var description = message.Text ?? ""; dialog.Close();
                    await _model.ExecuteActionAsync(root, () => new StashWriter().ExecuteAsync(context, action, stash, description));
                    if (_model.HasOperation) ShowConflictsClicked(null, new RoutedEventArgs());
                };
                actions.Children.Add(button);
            }
            var panel = new Grid { Margin = new Thickness(16), RowSpacing = 12, RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto") };
            panel.Children.Add(new TextBlock { Text = "保存包含未跟踪文件，不包含忽略文件。应用尝试恢复原暂存状态。", TextWrapping = TextWrapping.Wrap });
            Grid.SetRow(message, 1); panel.Children.Add(message);
            Grid.SetRow(list, 2); panel.Children.Add(list);
            var footer = DialogChrome.Footer(actions); Grid.SetRow(footer, 3); panel.Children.Add(footer);
            dialog.Content = panel; DialogChrome.Apply(dialog);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex) { _model.ReportError(ex.Message); }
    }

}
