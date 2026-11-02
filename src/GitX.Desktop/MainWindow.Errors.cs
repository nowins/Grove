using Avalonia.Controls;
using Avalonia.Interactivity;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private async void ErrorDetailsClicked(object? sender, RoutedEventArgs e)
    {
        if (!_model.HasError) return;
        var message = _model.Error;
        var dialog = new Window { Title = "操作未完成", Width = 620, Height = 300, MinWidth = 420, MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = new Button { Content = "关闭", IsCancel = true };
        var retry = new Button { Content = "重新选择工作树…", IsVisible = message == _worktreeFailureMessage };
        close.Click += (_, _) => dialog.Close(false);
        retry.Click += (_, _) => dialog.Close(true);
        dialog.Content = DialogChrome.Layout(new TextBox { Text = message, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap }, DialogChrome.Buttons(retry, close));
        DialogChrome.Apply(dialog); dialog.Opened += (_, _) => close.Focus();
        if (await dialog.ShowDialog<bool>(InteractionWindow) && !_closed && _model.Error == message) await OpenWorktreesAsync();
    }
}
