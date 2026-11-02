using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace GitX.Desktop.Services;

// Code-built menus use the same geometry and sizing as the XAML toolbars.
internal static class IconContent
{
    public static Path Icon(string key) => new()
    {
        Classes = { "icon" }, Data = (Geometry)Application.Current!.FindResource(key)!,
        IsHitTestVisible = false
    };

    public static StackPanel Label(string key, string label) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = { Icon(key), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } }
    };
}
