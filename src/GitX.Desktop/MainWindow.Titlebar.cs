using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.VisualTree;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private TitlebarMetrics? _appliedTitlebarMetrics;

    private void InitializeTitlebar()
    {
        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaTitleBarHeightHint = -1;
            // Native fullscreen/restore can reposition buttons after the state
            // notification. Re-read after layout, changing margins only as needed.
            LayoutUpdated += (_, _) => ApplyTitlebarMetrics();
        }
        ApplyTitlebarMetrics();
        Opened += (_, _) => ApplyTitlebarMetrics();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowDecorationMarginProperty || e.Property == WindowStateProperty) ApplyTitlebarMetrics();
        };
    }

    private void ApplyTitlebarMetrics()
    {
        double? buttonsRight = null;
        if (OperatingSystem.IsMacOS() && TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle)
            buttonsRight = MacTitlebarGeometry.ReadButtonsRight(handle.NSWindow, handle.NSView, WindowState == WindowState.FullScreen);
        var metrics = TitlebarMetrics.ForPlatform(OperatingSystem.IsMacOS(), WindowDecorationMargin.Top, buttonsRight);
        if (_appliedTitlebarMetrics == metrics) return;
        _appliedTitlebarMetrics = metrics;
        MainLayout.RowDefinitions[0].Height = new(metrics.Height);
        MainToolbar.Margin = new(metrics.Left, 0, metrics.Right, 0);
        MainToolbar.Padding = new(0, metrics.VerticalPadding);
        if (!metrics.Compact) return;
        foreach (var button in MainToolbar.GetVisualDescendants().OfType<Button>())
        {
            button.MinHeight = 0; button.Height = metrics.Height; button.Padding = new(6, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
        }
    }
}
