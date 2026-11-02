using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data;
using Avalonia.Styling;
using GitX.Core;

namespace GitX.Desktop.Services;

internal static class WorktreeDialog
{
    public static Task<WorktreeEntry?> ShowAsync(Window owner, WorktreeSnapshot snapshot)
    {
        if (owner is MainWindow main) owner = main.InteractionWindow;
        var dialog = new Window { Title = "工作树 · Worktrees", Width = 720, Height = 440, MinWidth = 520,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = snapshot.Entries, SelectedItem = snapshot.Entries.FirstOrDefault(e => e.Current) };
        AutomationProperties.SetName(list, "仓库工作树");
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters = { new Setter(AutomationProperties.NameProperty, new Binding(nameof(WorktreeEntry.AccessibleName))) } });
        list.ItemTemplate = new FuncDataTemplate<WorktreeEntry>((entry, _) =>
        {
            if (entry is null) return new Border();
            var row = new StackPanel { Spacing = 4, Margin = new(4, 4) };
            var title = new Grid { ColumnDefinitions = new("*,Auto") };
            title.Children.Add(new TextBlock { Text = entry.BranchLabel, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(8, 0, 0, 0) };
            void Badge(string text, string brush) => badges.Children.Add(new TextBlock { Text = text, FontSize = UiTypography.SecondarySize, Foreground = (IBrush)Application.Current!.FindResource(brush)! });
            if (entry.Current) Badge("当前", "AccentBrush");
            if (entry.LockedReason is not null) Badge("已锁定", "MutedBrush");
            if (entry.UnavailableReason is not null) Badge("不可用", "ErrorTextBrush");
            Grid.SetColumn(badges, 1); title.Children.Add(badges); row.Children.Add(title);
            row.Children.Add(new TextBlock { Text = entry.Root, Classes = { "muted" }, FontSize = UiTypography.SecondarySize, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = entry.UnavailableReason ?? (entry.Head is { } sha ? sha[..Math.Min(12, sha.Length)] : "尚无提交"),
                FontFamily = entry.UnavailableReason is null ? (FontFamily)Application.Current!.FindResource("CodeFontFamily")! : UiTypography.Family,
                FontSize = entry.UnavailableReason is null ? UiTypography.GitLogMetaSize : UiTypography.SecondarySize,
                Foreground = (IBrush)Application.Current!.FindResource(entry.UnavailableReason is null ? "MutedBrush" : "ErrorTextBrush")!, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTip.SetTip(row, entry.Root + "\n" + entry.Head + (entry.LockedReason is { } reason ? "\n锁定原因：" + reason : "") +
                (entry.UnavailableReason is { } unavailable ? "\n" + unavailable : ""));
            return row;
        });
        var cancel = new Button { Content = "取消", IsCancel = true };
        var open = new Button { Content = "打开工作树", Classes = { "primary" } };
        void SelectionChanged() => open.IsEnabled = list.SelectedItem is WorktreeEntry { Current: false, UnavailableReason: null };
        list.SelectionChanged += (_, _) => SelectionChanged(); SelectionChanged();
        cancel.Click += (_, _) => dialog.Close(null);
        open.Click += (_, _) => { if (open.IsEnabled && list.SelectedItem is WorktreeEntry entry) dialog.Close(entry); };
        var body = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12 };
        body.Children.Add(new TextBlock { Text = "同一仓库的工作树", Classes = { "dialog-title" } });
        Grid.SetRow(list, 1); body.Children.Add(list);
        var note = new TextBlock { Text = "切换只打开所选目录，原工作树的本地更改保留。锁定状态不妨碍打开；失效或缺失的工作树不可打开。",
            Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(note, 2); body.Children.Add(note);
        dialog.Content = DialogChrome.Layout(body, DialogChrome.Buttons(cancel, open));
        DialogChrome.Apply(dialog); dialog.Opened += (_, _) => cancel.Focus(NavigationMethod.Tab);
        return dialog.ShowDialog<WorktreeEntry?>(owner);
    }
}
