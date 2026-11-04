using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
        var picker = lifetime.Windows.Single(w => w.Title == "最近文件"); picker.Close(); await Task.Delay(150);
        var web = typeof(MainWindow).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main) as NativeWebView;
        web?.Focus(); if (web is not null) await web.InvokeScript("window.groveNative.command(() => window.gitx.focus())");
        Check(MenuKey(window, "e", 14) == 0, "Command+E was not handled by Cocoa.");
        await UntilAsync(() => lifetime.Windows.Any(w => w.Title == "最近文件"));
        Check(lifetime.Windows.Count(w => w.Title == "最近文件") == 1, "Command+E opened duplicate pickers.");
        lifetime.Windows.Single(w => w.Title == "最近文件").Close(); await Task.Delay(150);
        Check(model.DocumentVersion == version, "Cancel changed the current document.");
        Samples.Add(new { phase = "native-recent-files", menuAction = true, cocoaCommandE = true, duplicateWindows = false, cancelledWithoutDocumentChange = true });
        // No Git write: hold an in-memory operation so Quit must use Closing's
        // existing write guard instead of a forced lifetime Shutdown.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = model.ExecuteActionAsync(model.Root!, async () => { await release.Task; return "isolated menu probe; no Git write"; });
        await UntilAsync(() => model.IsWriting);
        try
        {
            var busy = ReadMenus();
            var file = busy.EnumerateArray().Single(g => g.GetProperty("title").GetString() == "文件");
            Check(!file.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("title").GetString() == "打开本地仓库…").GetProperty("enabled").GetBoolean(), "Open repository enabled during a Git operation.");
            Check(MenuKey(window, "q", 12) == 0, "Command+Q equivalent unavailable."); await Task.Delay(250);
            Check(main.IsVisible && model.IsWriting, "Native Quit bypassed the write guard.");
            Samples.Add(new { phase = "native-command-state-and-quit-guard", busyMenu = busy, quitKeptWindowOpen = true });
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
    [DllImport("grove-chrome-probe", EntryPoint = "grove_menu_snapshot")] private static extern int MenuSnapshot(byte[] output, int capacity);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_invoke_menu")] private static extern int InvokeMenu([MarshalAs(UnmanagedType.LPUTF8Str)] string group, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_menu_key")] private static extern int MenuKey(nint window, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, ushort code);
}
