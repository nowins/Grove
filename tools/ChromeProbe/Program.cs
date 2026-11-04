using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitX.Desktop;
using GitX.Desktop.ViewModels;

internal static partial class Program
{
    private static readonly List<object> Samples = [];
    private static int _exit;
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || args.Length != 2) throw new ArgumentException("macOS: expected result.json and native helper path.");
        var result = Path.GetFullPath(args[0]); var library = Path.GetFullPath(args[1]);
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, assembly, search) => name == "grove-chrome-probe" ? NativeLibrary.Load(library) : 0);
        var fixture = Path.Combine(Path.GetTempPath(), "grove-chrome-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(fixture, "演示项目-long-repository-name"); Directory.CreateDirectory(repository);
        File.WriteAllText(Path.Combine(repository, "sample.cs"), "class Fixture { int Value => 42; }\n");
        var init = new ProcessStartInfo("git") { WorkingDirectory = repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        init.ArgumentList.Add("init"); init.ArgumentList.Add("-b"); init.ArgumentList.Add("main");
        using (var git = Process.Start(init)!) { git.StandardOutput.ReadToEnd(); var error = git.StandardError.ReadToEnd(); git.WaitForExit(); if (git.ExitCode != 0) throw new IOException(error); }
        Environment.SetEnvironmentVariable("GITX_DATA_DIR", Path.Combine(fixture, "profile"));
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () => { try { await Task.Delay(TimeSpan.FromSeconds(90), stop.Token); Save(result, new { passed = false, error = "Native chrome probe timed out." }); Environment.Exit(23); } catch (OperationCanceledException) { } });
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().AfterSetup(builder => Dispatcher.UIThread.Post(async () =>
            {
                var desktop = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;
                try
                {
                    await Task.Delay(800); var main = (MainWindow)desktop.MainWindow!;
                    await RunAsync(main);
                    Save(result, new { passed = true, kind = "actual Grove window; AppKit frames, synthetic NSEvents and native button actions; no physical input", os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), samples = Samples });
                }
                catch (Exception error) { _exit = 1; Save(result, new { passed = false, error = error.ToString(), samples = Samples }); }
                finally { stop.Cancel(); desktop.Shutdown(_exit); }
            }, DispatcherPriority.ApplicationIdle)).StartWithClassicDesktopLifetime([repository], ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception error)
        {
            _exit = 1;
            Save(result, new { passed = false, error = error.ToString(), samples = Samples });
        }
        finally
        {
            stop.Cancel();
            var boundary = Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + Path.DirectorySeparatorChar + "grove-chrome-";
            if (!Path.GetFullPath(fixture).StartsWith(boundary, StringComparison.Ordinal)) throw new IOException("Fixture cleanup boundary failed.");
            Directory.Delete(fixture, true);
        }
        return _exit;
    }
    private static async Task RunAsync(MainWindow main)
    {
        var model = (MainViewModel)typeof(MainWindow).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        await UntilAsync(() => model.Root is not null && !model.Busy);
        var handle = main.TryGetPlatformHandle() as IMacOSTopLevelPlatformHandle ?? throw new IOException("Native Mac window handle missing.");
        var window = handle.NSWindow; var view = handle.NSView;
        foreach (var width in new[] { 1380d, 1100d, 880d })
        {
            main.Width = width; await Task.Delay(250); main.UpdateLayout(); Snapshot(main, window, view, "width-" + width);
            foreach (var name in new[] { "RepositoryButton", "BranchButton" })
            {
                var button = main.FindControl<Button>(name)!; var origin = button.TranslatePoint(default, main)!.Value;
                Check(ClickHeader(window, view, origin.X + button.Bounds.Width / 2, origin.Y + button.Bounds.Height / 2) == 0, "Native header event failed: " + name);
                await UntilAsync(() => typeof(MainWindow).GetField("_shellPopup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main) is Avalonia.Controls.Primitives.PopupFlyoutBase { IsOpen: true });
                Samples.Add(new { phase = "native-header-events", width, button = name, menuOpened = true });
                typeof(MainWindow).GetMethod("CloseShellPopup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [false]);
                await Task.Delay(100);
            }
        }
        await VerifyNativeMenusAsync(main, model, window);
        Check(Click(window, 1) == 0, "Native minimize button could not be invoked.");
        await UntilAsync(() => main.WindowState == WindowState.Minimized);
        Samples.Add(new { phase = "native-minimize", state = main.WindowState.ToString() });
        main.WindowState = WindowState.Normal; await UntilAsync(() => main.WindowState == WindowState.Normal); await Task.Delay(350);
        Check(Click(window, 2) == 0, "Native zoom/fullscreen button could not be invoked.");
        await UntilAsync(() => main.WindowState is WindowState.Maximized or WindowState.FullScreen);
        await Task.Delay(700); main.UpdateLayout(); Snapshot(main, window, view, "native-zoom");
        main.WindowState = WindowState.Normal; await UntilAsync(() => main.WindowState == WindowState.Normal); await Task.Delay(700);
        await UntilAsync(() =>
        {
            var rects = new NativeRect[3];
            return Read(window, view, rects, new byte[256], 256, out _) == 0 && rects.All(r => r.Visible != 0) && main.WindowDecorationMargin.Top > 0;
        });
        main.UpdateLayout(); Snapshot(main, window, view, "restored");
        Check(!model.IsWriting, "Unexpected Git write.");
        Check(Click(window, 0) == 0, "Native close button could not be invoked.");
        await UntilAsync(() => !main.IsVisible); Samples.Add(new { phase = "native-close", closed = true });
    }
    private static void Snapshot(MainWindow main, nint window, nint view, string phase)
    {
        Check(!main.FindControl<Button>("MainMenuButton")!.IsVisible, "Mac hamburger duplicates the native menu.");
        var system = new NativeRect[3]; var menuName = new byte[256];
        Check(Read(window, view, system, menuName, menuName.Length, out var scale) == 0, "Could not read native caption frames.");
        var appMenu = Encoding.UTF8.GetString(menuName).TrimEnd('\0');
        Samples.Add(new { phase = phase + "-native", width = main.Bounds.Width, scale, appMenu, nativeHeight = main.WindowDecorationMargin.Top, trafficLights = system });
        Check(appMenu == "Grove", "Native application menu is not Grove: " + appMenu);
        var managed = new Dictionary<string, Rect>();
        foreach (var name in new[] { "RepositoryButton", "BranchButton", "RemoteButton", "WorkspaceRefreshButton" })
        {
            var button = main.FindControl<Button>(name)!; var origin = button.TranslatePoint(default, main) ?? throw new IOException("Button outside window.");
            var box = new Rect(origin, button.Bounds.Size); managed.Add(name, box);
            Check(button.IsVisible && button.IsEnabled && box.X >= 0 && box.Right <= main.Bounds.Width + 1, "Clipped or unavailable header action: " + name);
            Check(Hit(window, view, box.Center.X, box.Center.Y) == 0, "Header action hits a native system button: " + name);
            foreach (var native in system.Where(r => r.Visible != 0)) Check(!box.Intersects(new Rect(native.X, native.Y, native.Width, native.Height)), "Header action overlaps a traffic light: " + name);
        }
        var visible = system.Where(r => r.Visible != 0).ToArray();
        if (main.WindowState != WindowState.FullScreen)
        {
            Check(visible.Length == 3 && visible.All(r => r.Hit != 0), "Traffic-light centers are not hit-testable.");
            var center = visible.Average(r => r.Y + r.Height / 2);
            Check(managed.Values.All(r => Math.Abs(r.Center.Y - center) <= 1.5), "Native and managed header controls are not vertically aligned.");
        }
        Check(!managed["RepositoryButton"].Intersects(managed["BranchButton"]), "Repository and branch controls overlap.");
        var repository = main.FindControl<Button>("RepositoryButton")!;
        var label = repository.GetVisualDescendants().OfType<TextBlock>().First();
        Samples.Add(new { phase, width = main.Bounds.Width, state = main.WindowState.ToString(), scale, appMenu, nativeHeight = main.WindowDecorationMargin.Top,
            trafficLights = system, controls = managed.ToDictionary(x => x.Key, x => new { x.Value.X, x.Value.Y, x.Value.Width, x.Value.Height }), projectText = label.Text, projectTextWidth = label.Bounds.Width,
            rightGap = main.Bounds.Width - managed["WorkspaceRefreshButton"].Right });
    }
    private static async Task UntilAsync(Func<bool> condition)
    { for (var i = 0; i < 160; i++) { if (condition()) return; await Task.Delay(50); } throw new TimeoutException("Native window transition did not complete."); }
    private static void Check(bool value, string error) { if (!value) throw new IOException(error); }
    private static void Save(string path, object result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        File.WriteAllText(path, json);
        // The probe uses only its generated fixture. Keep failure diagnostics
        // available in CI logs even when an artifact cannot be downloaded.
        Console.WriteLine("GROVE_NATIVE_PROBE_RESULT");
        Console.WriteLine(json);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public double X, Y, Width, Height; public int Visible, Hit; }
    [DllImport("grove-chrome-probe", EntryPoint = "grove_read_chrome")] private static extern int Read(nint window, nint view, [Out] NativeRect[] buttons, [Out] byte[] menu, int capacity, out double scale);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_hit_system_button")] private static extern int Hit(nint window, nint view, double x, double y);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_click_system_button")] private static extern int Click(nint window, int button);
    [DllImport("grove-chrome-probe", EntryPoint = "grove_click_header")] private static extern int ClickHeader(nint window, nint view, double x, double y);
}
