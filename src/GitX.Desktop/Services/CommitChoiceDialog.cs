using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;

namespace GitX.Desktop.Services;

internal static class CommitChoiceDialog
{
    public static Task<string?> ShowAsync(Window owner, string title, string description, IReadOnlyList<CommitEntry> commits)
    {
        if (owner is MainWindow main) owner = main.InteractionWindow;
        var dialog = new Window { Title = title, Width = 640, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var choices = new ListBox { ItemsSource = commits.Select((c, i) => $"{i + 1}. {c.ShortSha}  {c.Subject}").ToArray(), SelectedIndex = 0, Height = 200 };
        Avalonia.Automation.AutomationProperties.SetName(choices, title);
        var cancel = new Button { Content = "取消", IsCancel = true };
        var accept = new Button { Content = "选择", Classes = { "primary" } };
        cancel.Click += (_, _) => dialog.Close(null);
        accept.Click += (_, _) => { if (choices.SelectedIndex >= 0) dialog.Close(commits[choices.SelectedIndex].Sha); };
        dialog.Content = DialogChrome.Layout(new StackPanel { Spacing = 12, Children = {
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }, choices } }, DialogChrome.Buttons(cancel, accept));
        DialogChrome.Apply(dialog); dialog.Opened += (_, _) => choices.Focus();
        return dialog.ShowDialog<string?>(owner);
    }
}
