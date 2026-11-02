using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitX.Desktop.Services;

// Apply after assigning Content, before Show/ShowDialog. Only presentation is
// shared: each caller retains its result, focus and Closing/write protection.
internal static class DialogChrome
{
    public static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var button in buttons) { button.Classes.Add("dialog-action"); panel.Children.Add(button); }
        return panel;
    }

    public static Border Footer(Control actions) => new() { Classes = { "dialog-footer" }, Child = actions };

    public static Grid Layout(Control body, Control actions)
    {
        var panel = new Grid { Margin = new Thickness(16), RowDefinitions = new("*,Auto"), RowSpacing = 12 };
        panel.Children.Add(body);
        var footer = Footer(actions); Grid.SetRow(footer, 1); panel.Children.Add(footer);
        return panel;
    }

    public static void Apply(Window window)
    {
        var content = window.Content as Control ?? throw new InvalidOperationException("Dialog content must be a control.");
        window.Content = null;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = 32;
        var app = Application.Current!;
        window.WindowDecorationsTheme = (ControlTheme)app.FindResource(window.CanResize ? "GitXWindowChrome" : "GitXDialogChrome")!;
        if (!window.CanResize) window.CanMinimize = false;
        window.Background = (IBrush)app.FindResource("PanelBrush")!;
        window.Classes.Add("gitx-dialog");
        window.Content = new Border { Padding = new Thickness(0, 32, 0, 0), Child = content };
        // Explicit heights used to exclude the system title; keep body space.
        if (!double.IsNaN(window.Height)) window.Height += 32;
        if (window.MinHeight > 0) window.MinHeight += 32;
    }
}
