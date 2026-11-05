using System.Runtime.InteropServices;

namespace GitX.Desktop.Services;

// Read only our own NSWindow/NSView on the UI thread. Cocoa coordinates are
// logical points, matching Avalonia DIPs; never multiply by backingScaleFactor.
internal static class MacTitlebarGeometry
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static double? ReadButtonsRight(nint window, nint view, bool fullScreen)
    {
        if (!OperatingSystem.IsMacOS() || window == 0 || view == 0) return null;
        var right = 0d;
        var visible = 0;
        for (nint index = 0; index < 3; index++)
        {
            var button = SendIndex(window, Selector("standardWindowButton:"), index);
            if (button == 0) return null;
            if (SendBool(button, Selector("isHiddenOrHasHiddenAncestor")) != 0) continue;
            var bounds = ReadRect(button, Selector("bounds"));
            var rect = ConvertRect(view, Selector("convertRect:fromView:"), bounds, button);
            if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Width) || rect.X < 0 || rect.Width <= 0) return null;
            right = Math.Max(right, rect.X + rect.Width);
            visible++;
        }
        // During native transitions a partial set is not a reliable safe area.
        return visible == 3 ? right : visible == 0 && fullScreen ? 0 : null;
    }

    private static NativeRect ReadRect(nint target, nint selector)
    {
        // NSRect is 32 bytes: Intel uses the stret ABI, Apple Silicon does not.
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            SendRectIntel(out var rect, target, selector);
            return rect;
        }
        return SendRect(target, selector);
    }

    private static NativeRect ConvertRect(nint target, nint selector, NativeRect rect, nint source)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            ConvertRectIntel(out var result, target, selector, rect, source);
            return result;
        }
        return ConvertRectArm(target, selector, rect, source);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public double X, Y, Width, Height; }
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendIndex(nint target, nint selector, nint index);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NativeRect SendRect(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectIntel(out NativeRect result, nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NativeRect ConvertRectArm(nint target, nint selector, NativeRect rect, nint source);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void ConvertRectIntel(out NativeRect result, nint target, nint selector, NativeRect rect, nint source);
}
