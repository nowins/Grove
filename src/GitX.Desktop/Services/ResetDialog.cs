using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;

namespace GitX.Desktop.Services;

internal static class ResetDialog
{
    public static Task<HistoryAction?> ShowAsync(Window owner, ActionContext context, CommitEntry commit)
    {
        if (owner is MainWindow main) owner = main.InteractionWindow;
        var dialog = new Window { Title = "Git Reset", Width = 540, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var selected = HistoryAction.ResetMixed;
        var confirm = new Button { Content = "Reset", Classes = { "primary" } };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var body = new StackPanel { Spacing = 12 };
        var branch = context.Detached ? $"分离 HEAD · {context.Head?[..8]}" : context.Branch;
        var summary = $"{branch} → {commit.ShortSha}  “{commit.Subject}” · {commit.Author}";
        var heading = new TextBlock { Text = summary, FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(heading, summary); body.Children.Add(heading);
        var location = new TextBlock { Text = "工作树：" + context.Root, Classes = { "dialog-hint" },
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(location, context.Root); body.Children.Add(location);
        if (context.Detached) body.Children.Add(new TextBlock { Text = "当前为分离 HEAD；Reset 只移动此工作树的 HEAD，不移动分支。",
            Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "将当前 HEAD 移动到所选提交，并按所选模式更新索引和工作区：", TextWrapping = TextWrapping.Wrap });
        var modes = new StackPanel { Spacing = 10 };
        var radios = new List<RadioButton>();
        foreach (var (action, name, description) in new[]
        {
            (HistoryAction.ResetSoft, "Soft", "保留文件和索引，提交之间的差异保留为已暂存。"),
            (HistoryAction.ResetMixed, "Mixed", "保留文件并重置索引，差异变为未暂存。"),
            (HistoryAction.ResetHard, "Hard", "文件和索引恢复到所选提交；未提交改动会被丢弃。执行前先备份受影响文件和索引。"),
            (HistoryAction.ResetKeep, "Keep", "保留不冲突的工作区修改并重置索引；与 HEAD 到目标提交间的差异重叠时拒绝操作。")
        })
        {
            var row = new StackPanel { Spacing = 2 };
            var hint = new TextBlock { Text = description, Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap, Margin = new(28, 0, 0, 0) };
            if (action == HistoryAction.ResetHard) hint.Foreground = (IBrush)Application.Current!.FindResource("ErrorTextBrush")!;
            var radio = new RadioButton { Content = name, GroupName = "ResetMode", IsChecked = action == selected,
                MinHeight = 20, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(radio, name + "。" + description);
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true) return;
                selected = action;
                confirm.Content = action == HistoryAction.ResetHard ? "备份并 Reset" : "Reset";
            };
            hint.Tapped += (_, _) => { radio.IsChecked = true; radio.Focus(NavigationMethod.Pointer); };
            row.Children.Add(radio); row.Children.Add(hint); modes.Children.Add(row); radios.Add(radio);
        }
        for (var index = 0; index < radios.Count; index++)
        {
            var current = index;
            radios[index].KeyDown += (_, key) =>
            {
                var step = key.Key is Key.Down or Key.Right ? 1 : key.Key is Key.Up or Key.Left ? -1 : 0;
                if (step == 0 || key.KeyModifiers != KeyModifiers.None) return;
                var next = radios[(current + step + radios.Count) % radios.Count];
                next.IsChecked = true; next.Focus(NavigationMethod.Tab); key.Handled = true;
            };
        }
        body.Children.Add(modes);
        cancel.Click += (_, _) => dialog.Close(null);
        confirm.Click += (_, _) => dialog.Close((HistoryAction?)selected);
        dialog.Content = DialogChrome.Layout(body, DialogChrome.Buttons(confirm, cancel));
        DialogChrome.Apply(dialog);
        // Keep GitX's existing destructive-confirmation focus: opening a menu
        // and pressing Enter must not rewrite history.
        dialog.Opened += (_, _) => cancel.Focus(NavigationMethod.Tab);
        return dialog.ShowDialog<HistoryAction?>(owner);
    }
}
