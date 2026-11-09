using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;

namespace GitX.Desktop.Services;

internal static class HistoryBatchDialog
{
    public static Task<bool> ShowAsync(Window owner, string title, HistoryBatchPlan plan)
    {
        if (owner is MainWindow main) owner = main.InteractionWindow;
        var dialog = new Window { Title = title, Width = 650, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var description = plan.Action == HistoryAction.CherryPick ? "按从旧到新的图顺序应用所选提交，逐个创建提交。" : "按从新到旧的图顺序创建反向提交，保留原历史。";
        var list = new ListBox { ItemsSource = plan.Commits.Select((c, i) => $"{i + 1}. {c.ShortSha}  {c.Subject}").ToArray(), Height = Math.Clamp(plan.Commits.Count * 26, 52, 250) };
        Avalonia.Automation.AutomationProperties.SetName(list, "提交执行顺序");
        var cancel = new Button { Content = "取消", IsCancel = true };
        var accept = new Button { Content = "执行", Classes = { "primary" } };
        cancel.Click += (_, _) => dialog.Close(false); accept.Click += (_, _) => dialog.Close(true);
        dialog.Content = DialogChrome.Layout(new StackPanel { Spacing = 12, Children = {
            new TextBlock { Text = $"当前分支：{plan.Context.Branch}\n{description}" + (plan.NeedsMainline ? $"\n主线父提交：{plan.Mainline}" : ""), TextWrapping = TextWrapping.Wrap }, list,
            new TextBlock { Text = "遇到冲突或空改动时暂停，可继续、跳过当前提交或中止整批操作。", Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap }
        } }, DialogChrome.Buttons(cancel, accept));
        DialogChrome.Apply(dialog); return dialog.ShowDialog<bool>(owner);
    }
}
