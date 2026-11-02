using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;

namespace GitX.Desktop.Services;

internal static class ConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, string title, string message, string accept)
    {
        if (owner is MainWindow main) owner = main.InteractionWindow;
        var dialog = new Window { Title = title, Width = 500, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "取消", IsCancel = true, Classes = { "dialog-action" } };
        var confirm = new Button { Content = accept, Classes = { "primary", "dialog-action" } };
        cancel.Click += (_, _) => dialog.Close(false); confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = DialogChrome.Layout(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, DialogChrome.Buttons(cancel, confirm));
        DialogChrome.Apply(dialog); dialog.Opened += (_, _) => cancel.Focus(NavigationMethod.Tab);
        return dialog.ShowDialog<bool>(owner);
    }
}
