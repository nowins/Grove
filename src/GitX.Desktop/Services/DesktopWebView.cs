using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GitX.Desktop.Services;

// All editor windows use the same platform engine and isolated application data.
internal static class DesktopWebView
{
    public static void Configure(NativeWebView view, string? profile = null)
    {
        profile ??= Environment.GetEnvironmentVariable("GITX_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitX", "WebView2");
        if (OperatingSystem.IsLinux())
        {
            if (!NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out var library))
                throw new IOException("Linux 编辑器需要 WebKitGTK 4.1 和 X11/XWayland；请安装发行版的 libwebkit2gtk-4.1-0 软件包后重启 Grove。详见 LINUX.md。");
            NativeLibrary.Free(library);
            view.AdapterPreference = [WebViewAdapterType.WebKitGtk];
        }
        view.EnvironmentRequested += (_, e) =>
        {
            e.EnableDevTools = false;
            if (e is WindowsWebView2EnvironmentRequestedEventArgs windows) windows.UserDataFolder = profile;
            // Use distro-supported GTK 3 / WebKitGTK on X11 or XWayland.
            // Do not require WPE 2.0 or disable its sandbox.
            if (e is GtkWebViewEnvironmentRequestedEventArgs gtk)
            {
                gtk.ForceX11GdkBackend = true;
                gtk.BaseDataDirectory = Path.Combine(profile, "webkit-data");
                gtk.BaseCacheDirectory = Path.Combine(profile, "webkit-cache");
            }
        };
    }
}
