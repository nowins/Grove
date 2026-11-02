using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitX.Desktop.Services;

internal static class InputDialog
{
    // No default accept button: IME candidate Enter and multiline Enter never execute Git.
    public static Task<string?> ShowAsync(Window owner, string title, string description, string initial = "", bool multiline = false, bool secret = false, bool required = false, string? inputLabel = null, string acceptText = "确定")
    {
        if (owner is MainWindow main) owner = main.InteractionWindow;
        var dialog = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var input = new TextBox { Text = initial, AcceptsReturn = multiline, Height = multiline ? 130 : 28, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, PasswordChar = secret ? '●' : '\0' };
        var cancel = new Button { Content = "取消", IsCancel = true, Classes = { "dialog-action" } }; var accept = new Button { Content = acceptText, Classes = { "primary", "dialog-action" } };
        accept.IsEnabled = !required || !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => accept.IsEnabled = !required || !string.IsNullOrWhiteSpace(input.Text);
        cancel.Click += (_, _) => dialog.Close(null); accept.Click += (_, _) => dialog.Close(input.Text ?? "");
        var field = new StackPanel { Spacing = 6 };
        if (inputLabel is not null) field.Children.Add(new TextBlock { Text = inputLabel, Classes = { "dialog-label" } });
        Avalonia.Automation.AutomationProperties.SetName(input, inputLabel ?? title);
        field.Children.Add(input);
        dialog.Content = DialogChrome.Layout(new StackPanel { Spacing = 12, Children = {
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }, field } }, DialogChrome.Buttons(cancel, accept));
        DialogChrome.Apply(dialog);
        dialog.Opened += (_, _) => input.Focus(); return dialog.ShowDialog<string?>(owner);
    }
}
