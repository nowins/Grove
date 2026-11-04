using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using GitX.Desktop;
using GitX.Desktop.ViewModels;

internal static partial class Program
{
    private static async Task VerifyNativeMenusAsync(MainWindow main, MainViewModel model, nint window)
    {
        var native = ReadMenus();
        var groups = native.EnumerateArray().ToArray();
        foreach (var title in new[] { "文件", "视图", "搜索", "Git", "窗口", "帮助" }) Check(groups.Any(g => g.GetProperty("title").GetString() == title), "Missing native menu: " + title);
        var appItems = groups[0].GetProperty("items").EnumerateArray().ToArray();
        Check(appItems.Count(i => i.GetProperty("title").GetString() == "关于 Grove…") == 1, "About should occur once in Grove menu.");
        Check(appItems.Count(i => i.GetProperty("key").GetString() == "q") == 1, "Quit key must occur exactly once.");
        Check(!native.GetRawText().Contains("About Avalonia", StringComparison.Ordinal), "Framework About entry leaked.");
        Samples.Add(new { phase = "native-menu-inventory", native });
        Check(InvokeMenu("视图", "Git 输出") == 0, "Native output command unavailable."); await Task.Delay(200);
        Check(((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows.Count <= 1, "Output unexpectedly opens a separate window.");
        await model.OpenEditorEntryAsync(new("sample.cs", "sample.cs", GitX.Core.ComparisonKind.WorkingFile));
        await UntilAsync(() => model.CurrentDocument is { IsNotice: false });
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        // A real native menu command and a Cocoa Command+E equivalent both open
        // exactly one picker. Merely selecting/cancelling cannot change tabs.
        var version = model.DocumentVersion;
        Check(InvokeMenu("搜索", "最近文件…") == 0, "Native recent-files action unavailable.");
        await UntilAsync(() => lifetime.Windows.Any(w => w.Title == "最近文件"));
        var picker = lifetime.Windows.Single(w => w.Title == "最近文件");
        var closeEvents = 0; picker.Closing += (_, _) => closeEvents++;
        picker.Close(); await Task.Delay(150);
        Check(closeEvents == 1 && !picker.IsVisible, "Recent picker must close once without deactivation reentry.");
        var web = typeof(MainWindow).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main) as NativeWebView;
        Check(ActivateTestWindow(window) == 0, "Could not activate owned test window.");
        await UntilAsync(() => main.IsActive);
        web?.Focus(); if (web is not null) await web.InvokeScript("window.groveNative.command(() => window.gitx.focus())");
        await Task.Delay(150);
        var clickCount = 0;
        var allMenuClicks = new List<string?>();
        foreach (var group in NativeMenu.GetMenu(main)!.Items.OfType<NativeMenuItem>())
            if (group.Menu is { } menu) foreach (var action in menu.Items.OfType<NativeMenuItem>())
                action.Click += (_, _) => allMenuClicks.Add(action.Header);
        var recentItem = NativeMenu.GetMenu(main)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "搜索").Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "最近文件…");
        recentItem.Click += (_, _) => clickCount++;
        Samples.Add(new { phase = "before-cocoa-command-e", focus = ReadFocus(window), enabled = recentItem.IsEnabled, canExecute = recentItem.Command?.CanExecute(null), version = model.DocumentVersion });
        Check(MenuKey(window, "e", 14) == 0, "Could not queue Command+E in the active owned application.");
        try { await UntilAsync(() => lifetime.Windows.Any(w => w.Title == "最近文件")); }
        catch
        {
            var field = typeof(MainWindow).GetField("_recentFilesWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main) as Window;
            Samples.Add(new { phase = "failed-cocoa-command-e", clickCount, allMenuClicks, focus = ReadFocus(window), version = model.DocumentVersion, error = model.Error, trackedPicker = field is null ? null : new { field.IsVisible, field.IsActive }, windows = lifetime.Windows.Select(w => new { w.Title, w.IsActive, w.IsVisible }).ToArray(), menu = ReadMenus() });
            throw;
        }
        Check(lifetime.Windows.Count(w => w.Title == "最近文件") == 1, "Command+E opened duplicate pickers.");
        lifetime.Windows.Single(w => w.Title == "最近文件").Close(); await Task.Delay(150);
        Check(model.DocumentVersion == version, "Cancel changed the current document.");
        Samples.Add(new { phase = "native-recent-files", menuAction = true, nsApplicationCommandE = true, duplicateWindows = false, cancelledWithoutDocumentChange = true });
        // No Git write: hold an in-memory operation so Quit must use Closing's
        // existing write guard instead of a forced lifetime Shutdown.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = model.ExecuteActionAsync(model.Root!, async () => { await release.Task; return "isolated menu probe; no Git write"; });
        await UntilAsync(() => model.IsWriting);
        try
        {
            Check(ActivateTestWindow(window) == 0, "Could not activate owned window for busy menu inspection.");
            await UntilAsync(() => main.IsActive);
            var busy = ReadMenus();
            var file = busy.EnumerateArray().Single(g => g.GetProperty("title").GetString() == "文件");
            Check(!file.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("title").GetString() == "打开本地仓库…").GetProperty("enabled").GetBoolean(), "Open repository enabled during a Git operation.");
            Check(ActivateTestWindow(window) == 0, "Could not activate owned window for Quit guard.");
            await UntilAsync(() => main.IsActive);
            var quitAttempts = 0; var quitCancelled = false; var quitMenuClicks = 0;
            var shutdownRequests = new List<object>();
            var quitKeys = new List<object>();
            var applicationMenu = NativeMenu.GetMenu(Application.Current!)!;
            var quitItem = applicationMenu.Items.OfType<NativeMenuItem>().Single(i => i.Gesture is { Key: Key.Q, KeyModifiers: KeyModifiers.Meta });
            void ObserveClosing(object? sender, WindowClosingEventArgs args) { quitAttempts++; quitCancelled |= args.Cancel; }
            void ObserveShutdown(object? sender, ShutdownRequestedEventArgs args) => shutdownRequests.Add(new { args.Cancel });
            void ObserveQuitClick(object? sender, EventArgs args) => quitMenuClicks++;
            void ObserveKey(object? sender, KeyEventArgs args)
            {
                if (args.Key == Key.Q) quitKeys.Add(new { key = args.Key.ToString(), modifiers = args.KeyModifiers.ToString(), args.Handled, source = args.Source?.GetType().Name });
            }
            main.Closing += ObserveClosing;
            lifetime.ShutdownRequested += ObserveShutdown;
            quitItem.Click += ObserveQuitClick;
            main.AddHandler(InputElement.KeyDownEvent, ObserveKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            try
            {
                // Check the native menu action and keyboard dispatch separately.
                // Both must reach the same guard exactly once.
                Check(InvokeMenu("", quitItem.Header!) == 0, "Native Quit action unavailable.");
                await UntilAsync(() => quitAttempts > 0);
                Check(quitAttempts == 1 && quitCancelled && main.IsVisible && model.IsWriting, "Native Quit action bypassed or duplicated the write guard.");
                Samples.Add(new { phase = "native-quit-menu-guard", quitAttempts, quitCancelled, quitMenuClicks, shutdownRequests = shutdownRequests.ToArray() });
                quitAttempts = 0; quitCancelled = false; quitMenuClicks = 0; shutdownRequests.Clear(); quitKeys.Clear();
                Check(ActivateTestWindow(window) == 0, "Could not reactivate owned window after Quit action.");
                await UntilAsync(() => main.IsActive);
                web?.Focus(); if (web is not null) await web.InvokeScript("window.groveNative.command(() => window.gitx.focus())");
                await Task.Delay(150);
                Check(FocusTestWebView(window) == 0, "Could not focus the owned WKWebView for the Quit regression.");
                await UntilAsync(() => ReadFocus(window).GetProperty("firstResponder").GetString() == "ManagedWKWebView");
                Samples.Add(new { phase = "before-cocoa-command-q", focus = ReadFocus(window), quitItem.Header, quitItem.IsEnabled,
                    mainOwner = main.Owner?.GetType().Name, shutdownMode = lifetime.ShutdownMode.ToString(), windows = lifetime.Windows.Select(w => new { w.Title, w.IsActive, w.IsVisible, owner = w.Owner?.GetType().Name }).ToArray(), menu = ReadMenus() });
                Check(MenuKey(window, "q", 12) == 0, "Could not queue Command+Q in the owned application.");
                try { await UntilAsync(() => quitAttempts > 0); }
                catch
                {
                    Samples.Add(new { phase = "failed-cocoa-command-q", quitAttempts, quitCancelled, quitMenuClicks, shutdownRequests = shutdownRequests.ToArray(),
                        quitKeys = quitKeys.ToArray(), allMenuClicks = allMenuClicks.ToArray(), focus = ReadFocus(window), mainOwner = main.Owner?.GetType().Name,
                        writing = model.IsWriting, windows = lifetime.Windows.Select(w => new { w.Title, w.IsActive, w.IsVisible, owner = w.Owner?.GetType().Name }).ToArray(), menu = ReadMenus() });
                    // Diagnose the native Quit action separately; it is never a
                    // substitute for the required Command+Q keyboard assertion.
                    try
                    {
                        var invoked = InvokeMenu("", quitItem.Header!);
                        await Task.Delay(250);
                        Samples.Add(new { phase = "quit-action-after-key-failure", invoked, quitAttempts, quitCancelled, quitMenuClicks,
                            shutdownRequests = shutdownRequests.ToArray(), writing = model.IsWriting, main.IsVisible });
                    }
                    catch (Exception diagnosticError) { Samples.Add(new { phase = "quit-action-diagnostic-error", error = diagnosticError.ToString() }); }
                    throw;
                }
                await Task.Delay(150);
                Check(quitAttempts == 1 && shutdownRequests.Count == 1 && quitCancelled && main.IsVisible && model.IsWriting, "Command+Q bypassed or duplicated the write guard.");
                Samples.Add(new { phase = "native-command-state-and-quit-guard", busyMenu = busy, quitAttempts, quitCancelled, quitMenuClicks,
                    shutdownRequests = shutdownRequests.ToArray(), quitKeys = quitKeys.ToArray(), quitKeptWindowOpen = true });
            }
            finally
            {
                main.Closing -= ObserveClosing;
                lifetime.ShutdownRequested -= ObserveShutdown;
                quitItem.Click -= ObserveQuitClick;
                main.RemoveHandler(InputElement.KeyDownEvent, ObserveKey);
            }
        }
        finally { release.TrySetResult(); await task; }
        Check(InvokeMenu("", "关于 Grove…") == 0, "Native application About command unavailable.");
        await UntilAsync(() => lifetime.Windows.Any(w => w.Title == "关于 Grove"));
        lifetime.Windows.Single(w => w.Title == "关于 Grove").Close(); await Task.Delay(150);
    }
    private static JsonElement ReadMenus()
    {
        var bytes = new byte[32768]; Check(MenuSnapshot(bytes, bytes.Length) == 0, "Could not inspect actual NSMenu.");
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimEnd('\0')); return json.RootElement.Clone();
    }
    private static JsonElement ReadFocus(nint window)
    {
        var bytes = new byte[2048]; Check(TestWindowFocus(window, bytes, bytes.Length) == 0, "Could not inspect owned focus.");
        using var value = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimEnd('\0')); return value.RootElement.Clone();
    }
    [DllImport("grove-chrome-probe", EntryPoint = "grove_focus_test_webview")] private static extern int FocusTestWebView(nint window);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_activate_test_window")] private static extern int ActivateTestWindow(nint window);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_test_window_focus")] private static extern int TestWindowFocus(nint window, byte[] output, int capacity);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_menu_snapshot")] private static extern int MenuSnapshot(byte[] output, int capacity);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_invoke_menu")] private static extern int InvokeMenu([MarshalAs(UnmanagedType.LPUTF8Str)] string group, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_menu_key")] private static extern int MenuKey(nint window, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, ushort code);
}
