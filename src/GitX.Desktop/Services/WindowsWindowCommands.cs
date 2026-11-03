using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace GitX.Desktop.Services;

internal static class WindowsWindowCommands
{
    public static void Attach(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        new Registration(window).Install();
    }

    // A native subclass preserves the return values of other WndProc handlers.
    // Avalonia 12's multicast callback API retains only its last return value.
    private sealed class Registration(Window window)
    {
        private const nuint Identifier = 0x47525631;
        private readonly WindowCommandPolicy _policy = new();
        private SubclassProc _callback = null!;
        private nint _handle;

        public void Install()
        {
            _callback = Handle;
            _policy.Observe(ToState(window.WindowState));
            window.PropertyChanged += Changed; window.Opened += Opened; window.Closed += Closed;
            TryAttach();
        }
        private void Opened(object? sender, EventArgs e) => TryAttach();
        private void TryAttach()
        {
            if (_handle != 0 || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND", Handle: not 0 } platform) return;
            _handle = platform.Handle;
            if (!SetWindowSubclass(_handle, _callback, Identifier, 0)) { _handle = 0; throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        }
        private nint Handle(nint hwnd, uint message, nuint parameter, nint data, nuint identifier, nuint reference)
        {
            if (message == 0x0082) { RemoveWindowSubclass(hwnd, _callback, Identifier); _handle = 0; }
            if (message == 0x112 && window.IsEnabled && window.IsVisible &&
                _policy.TryGetTarget(unchecked((uint)parameter), ToState(window.WindowState), window.CanMinimize, out var target))
            {
                // Keep the native taskbar/system command and Avalonia's managed
                // state in one path, before custom caption handling consumes it.
                try { window.SetCurrentValue(Window.WindowStateProperty, FromState(target)); return 0; }
                catch (InvalidOperationException) { /* A closing window retains default OS handling. */ }
            }
            return DefSubclassProc(hwnd, message, parameter, data);
        }
        private void Changed(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        { if (e.Property == Window.WindowStateProperty) _policy.Observe(ToState(window.WindowState)); }
        private void Closed(object? sender, EventArgs e)
        {
            if (_handle != 0) RemoveWindowSubclass(_handle, _callback, Identifier);
            _handle = 0; window.PropertyChanged -= Changed; window.Opened -= Opened; window.Closed -= Closed;
            GC.KeepAlive(_callback);
        }
    }

    public static bool CanFocus(Window window)
    {
        if (window.WindowState == WindowState.Minimized) return false;
        if (!OperatingSystem.IsWindows()) return true;
        var handle = window.TryGetPlatformHandle();
        return handle is null || handle.Handle == 0 || !IsIconic(handle.Handle);
    }
    private static WindowCommandState ToState(WindowState state) => state switch
    { WindowState.Minimized => WindowCommandState.Minimized, WindowState.Maximized => WindowCommandState.Maximized, WindowState.FullScreen => WindowCommandState.FullScreen, _ => WindowCommandState.Normal };
    private static WindowState FromState(WindowCommandState state) => state switch
    { WindowCommandState.Minimized => WindowState.Minimized, WindowCommandState.Maximized => WindowState.Maximized, WindowCommandState.FullScreen => WindowState.FullScreen, _ => WindowState.Normal };
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    private delegate nint SubclassProc(nint window, uint message, nuint parameter, nint data, nuint identifier, nuint reference);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint identifier, nuint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint identifier);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint parameter, nint data);
}
