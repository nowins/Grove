using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GitX.Core;

namespace GitX.Desktop.Services;

internal enum DesktopLocationAction { Reveal, Terminal }
internal enum DesktopPlatform { Windows, MacOS, Other }
internal sealed record DesktopLocation(string Root, string RelativePath, string FullPath, string Directory, bool IsDirectory);
internal sealed record DesktopLaunchPlan(DesktopLocation Location, DesktopLocationAction Action, string? Executable,
    IReadOnlyList<string> Arguments, string WorkingDirectory, bool NativeWindowsSelection = false, bool WaitForExit = false);

internal sealed class DesktopLocations
{
    internal Func<DesktopLaunchPlan, Task> Launch { get; set; } = LaunchDefaultAsync;
    public static DesktopPlatform Platform => OperatingSystem.IsWindows() ? DesktopPlatform.Windows : OperatingSystem.IsMacOS() ? DesktopPlatform.MacOS : DesktopPlatform.Other;
    public static string RevealLabel => Platform switch { DesktopPlatform.Windows => "在资源管理器中显示", DesktopPlatform.MacOS => "在 Finder 中显示", _ => "在文件管理器中显示" };

    public static DesktopLocation Resolve(string root, string relative, bool directory)
    {
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root)) throw new IOException("当前工作树目录已不存在。");
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("不能打开工作树之外或 Git 元数据中的路径。");
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = relative.Length == 0 && directory ? normalizedRoot : RepositoryReader.SafeWorktreePath(normalizedRoot, relative);
        if (directory ? !System.IO.Directory.Exists(full) : !File.Exists(full)) throw new IOException(directory ? "目录已不存在，请刷新项目树。" : "文件已不存在；未打开其他路径。");
        for (FileSystemInfo? current = directory ? new DirectoryInfo(full) : new FileInfo(full); current is not null; current = System.IO.Directory.GetParent(current.FullName))
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("暂不打开符号链接或目录联接，请在系统中核对目标。");
        return new(normalizedRoot, relative, full, directory ? full : Path.GetDirectoryName(full)!, directory);
    }

    internal static DesktopLaunchPlan Plan(DesktopLocation target, DesktopLocationAction action, DesktopPlatform platform, string? windowsSystemDirectory = null)
    {
        if (platform == DesktopPlatform.Windows)
        {
            if (action == DesktopLocationAction.Reveal) return new(target, action, null, [], target.Directory, NativeWindowsSelection: true);
            // No cd/-Command string: the untrusted path is only a process cwd.
            var system = windowsSystemDirectory ?? Environment.SystemDirectory;
            if (!Path.IsPathFullyQualified(system)) throw new IOException("无法确定系统终端位置。");
            return new(target, action, Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoLogo", "-NoExit", "-NoProfile"], target.Directory);
        }
        if (platform == DesktopPlatform.MacOS)
            return new(target, action, "/usr/bin/open", action == DesktopLocationAction.Reveal
                ? ["-R", target.FullPath] : ["-a", "/System/Applications/Utilities/Terminal.app", target.Directory], target.Directory, WaitForExit: true);
        // Generic file managers do not share a select-item CLI. Open the
        // containing directory; terminal alternatives inherit cwd without code.
        return new(target, action, action == DesktopLocationAction.Reveal ? "/usr/bin/xdg-open" : "/usr/bin/x-terminal-emulator",
            action == DesktopLocationAction.Reveal ? [target.IsDirectory ? target.FullPath : target.Directory] : [], target.Directory, WaitForExit: action == DesktopLocationAction.Reveal);
    }

    public Task OpenAsync(DesktopLocation target, DesktopLocationAction action) => Launch(Plan(target, action, Platform));
    private static async Task LaunchDefaultAsync(DesktopLaunchPlan plan)
    {
        // Recheck immediately before dispatch in case the item changed while
        // the caller was awaiting the background filesystem check.
        var target = Resolve(plan.Location.Root, plan.Location.RelativePath, plan.Location.IsDirectory);
        plan = Plan(target, plan.Action, Platform);
        if (plan.NativeWindowsSelection)
        {
            if (!OperatingSystem.IsWindows()) throw new IOException("此系统不支持 Windows 文件选择接口。");
            await RevealWindowsAsync(target.FullPath); return;
        }
        if (plan.Executable is null || !File.Exists(plan.Executable)) throw new IOException("系统文件管理器或终端不可用，未启动其他程序。");
        if (OperatingSystem.IsMacOS() && plan.Action == DesktopLocationAction.Terminal && !System.IO.Directory.Exists("/System/Applications/Utilities/Terminal.app"))
            throw new IOException("系统 Terminal 应用不可用，未安装或启动其他终端。");
        var start = new ProcessStartInfo(plan.Executable) { WorkingDirectory = target.Directory, UseShellExecute = false,
            CreateNoWindow = plan.WaitForExit, RedirectStandardError = plan.WaitForExit, RedirectStandardOutput = plan.WaitForExit };
        foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("系统应用未启动。");
        if (!plan.WaitForExit) return;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token); await output;
        var message = await error;
        if (process.ExitCode != 0) throw new IOException("系统应用未能打开目标：" + message.Trim());
    }

    [SupportedOSPlatform("windows")]
    private static Task RevealWindowsAsync(string path)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false; nint pidl = 0;
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2)); initialized = true;
                Marshal.ThrowExceptionForHR(SHParseDisplayName(path, 0, out pidl, 0, out _));
                // cidl=0 selects the exact item in its parent folder, including
                // directories. No command line or shell parsing of the path.
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, 0, 0));
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); if (initialized) CoUninitialize(); }
        }) { IsBackground = true, Name = "Grove file reveal" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int SHParseDisplayName(string name, nint bind, out nint item, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint children, uint flags);
}
