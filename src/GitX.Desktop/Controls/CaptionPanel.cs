using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitX.Desktop.Controls;

// The decorations template lives under TopLevelHost, outside the window's
// ordinary content. Observe its owning Window without changing caption actions.
public sealed class CaptionPanel : Panel
{
    private Window? _window;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window
            ?? e.RootVisual.GetVisualChildren().OfType<Window>().FirstOrDefault();
        if (_window is not null) _window.PropertyChanged += WindowChanged;
        // Avalonia assigns English automation names when it attaches parts.
        Dispatcher.UIThread.Post(UpdateState, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.PropertyChanged -= WindowChanged;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsActiveProperty || e.Property == Window.WindowStateProperty) UpdateState();
    }

    private void UpdateState()
    {
        if (_window is null) return;
        Classes.Set("integrated", _window is MainWindow);
        Classes.Set("inactive", !_window.IsActive);
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            var name = button.Name switch
            {
                "PART_MinimizeButton" => "最小化",
                "PART_MaximizeButton" => _window.WindowState == WindowState.Maximized ? "还原" : "最大化",
                "PART_CloseButton" => "关闭",
                _ => null
            };
            if (name is null) continue;
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
        }
    }
}
