using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace GitX.Desktop.Services;

internal static class NativeKeyboardFocus
{
    public static void LeaveEmbedded(Window window, Control embedded, int direction)
    {
        if (!window.IsActive || (!embedded.IsFocused && !HasEmbeddedFocus(window))) return;
        var visited = new HashSet<Control> { embedded };
        var current = embedded;
        // FindNextElement can return a disabled button (e.g. Save after saving).
        // Follow tab order until a control actually accepts focus, without ever
        // circling back into the native child HWND.
        while (window.FocusManager?.FindNextElement(direction < 0 ? NavigationDirection.Previous : NavigationDirection.Next,
            new FindNextElementOptions { FocusedElement = current, SearchRoot = window }) is Control target && visited.Add(target))
        {
            if (TryFocusControl(window, target)) return;
            current = target;
        }
    }

    // A mouse click inside an embedded HWND can leave Avalonia's logical focus
    // on the previous control. Check ownership, never enumerate WebView internals.
    public static bool HasEmbeddedFocus(Window window)
    {
        if (!OperatingSystem.IsWindows() || !window.IsActive) return false;
        var handle = window.TryGetPlatformHandle();
        var focused = GetFocus();
        return handle is { HandleDescriptor: "HWND" } && handle.Handle != IntPtr.Zero &&
            GetForegroundWindow() == handle.Handle && focused != IntPtr.Zero && IsChild(handle.Handle, focused);
    }

    // WebView2's adapter does not relinquish its HWND focus on Avalonia LostFocus.
    // Restore the owning window's keyboard queue as well as the logical control.
    public static bool TryFocusControl(Window window, Control target)
    {
        Dispatcher.UIThread.VerifyAccess();
        window = TopLevel.GetTopLevel(target) as Window ?? window;
        if (!window.IsActive) return false;
        if (!OperatingSystem.IsWindows()) return target.Focus(NavigationMethod.Directional);

        var handle = window.TryGetPlatformHandle();
        if (handle is not { HandleDescriptor: "HWND" } || handle.Handle == IntPtr.Zero ||
            GetForegroundWindow() != handle.Handle) return false;
        if (!target.Focus(NavigationMethod.Directional)) return false;
        // Only our already-active top-level window; never activate another window
        // or reach into WebView2's private controller/child HWND hierarchy.
        SetFocus(handle.Handle);
        return GetFocus() == handle.Handle;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
}
