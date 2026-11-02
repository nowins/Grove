using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;

namespace GitX.Desktop.Services;

internal static class UiMotion
{
    // Read on each open so a Windows accessibility change applies without restart.
    private static bool Enabled => !OperatingSystem.IsWindows() ||
        SystemParametersInfo(0x1042, 0, out var enabled, 0) && enabled;

    public static void PreparePopup(Control content)
    {
        if (!Enabled) return;
        content.Opacity = 0;
        content.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) }
        };
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
