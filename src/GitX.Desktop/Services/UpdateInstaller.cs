using System.Diagnostics;
using GitX.Core.Updates;

namespace GitX.Desktop.Services;

internal sealed record InstallerInspection(bool Signed, string Description);
internal static class UpdateInstaller
{
    // Preserve the OS download trust boundary. Checksums prove bytes, not publisher identity.
    public static async Task MarkDownloadedAsync(string path, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
            await File.WriteAllTextAsync(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://github.com/nowins/Grove/releases\r\n", token);
        else if (OperatingSystem.IsMacOS())
        {
            var quarantine = $"0083;{DateTimeOffset.UtcNow.ToUnixTimeSeconds():x};Grove;{Guid.NewGuid():D}";
            if ((await RunAsync("/usr/bin/xattr", ["-w", "com.apple.quarantine", quarantine, path], token)).ExitCode != 0) throw new IOException("无法保留 macOS 下载隔离标记，未开放安装。可在正式 Release 页面手动下载。");
        }
        else throw new IOException("当前系统暂不支持安装更新。");
    }
    public static async Task<InstallerInspection> InspectAsync(string path, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var result = await RunAsync(shell, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write((Get-AuthenticodeSignature -LiteralPath $env:GROVE_UPDATE_PACKAGE).Status.ToString())"], token, path);
            if (result.ExitCode != 0) throw new IOException("无法检查 Windows 安装包签名，未启动安装。");
            return result.Output.Trim() switch
            {
                "Valid" => new(true, "Windows 报告签名有效。仍会启动交互式安装程序。"),
                "NotSigned" => new(false, "此 Windows 安装包未签名。校验值只能确认下载内容，不代表发布者身份；Windows 可能显示安全提示。"),
                _ => throw new IOException("Windows 安装包签名无效或不受信任，已阻止启动。请通过正式 Release 页面核查。")
            };
        }
        if (OperatingSystem.IsMacOS())
        {
            if ((await RunAsync("/usr/bin/hdiutil", ["verify", path], token)).ExitCode != 0) throw new IOException("DMG 完整性检查失败，未打开镜像。");
            var display = await RunAsync("/usr/bin/codesign", ["-d", path], token);
            if (display.ExitCode != 0 && display.Error.Contains("not signed at all", StringComparison.Ordinal))
                return new(false, "此 DMG 没有已验证的开发者签名。此步骤未核验镜像内应用的签名或公证；macOS 可能阻止打开。将保留隔离标记，由系统处理信任。打开后需自行退出 Grove 并拖入 Applications 替换，不会后台覆盖应用。");
            if (display.ExitCode != 0 || (await RunAsync("/usr/bin/codesign", ["--verify", "--strict", path], token)).ExitCode != 0) throw new IOException("DMG 签名无效，已阻止打开。");
            return new(true, "DMG 签名检查通过；这不代表已核对 Grove 发布者或通过 Apple 公证。打开后请退出 Grove 并手动将应用拖入 Applications。");
        }
        throw new IOException("当前系统暂不支持安装更新。");
    }
    public static void OpenInteractive(string path)
    {
        // No /S, elevated execution, quarantine removal, app replacement or forced exit.
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
        { var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add(path); Process.Start(start); }
        else throw new IOException("当前系统暂不支持安装更新。");
    }
    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, string[] arguments, CancellationToken token, string? package = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (package is not null)
        {
            start.Environment["GROVE_UPDATE_PACKAGE"] = package;
            start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(executable)!, "Modules");
        }
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("无法启动系统验证工具。");
        using var cancellation = timeout.Token.Register(() =>
        { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        var output = ReadAsync(process.StandardOutput, timeout.Token); var error = ReadAsync(process.StandardError, timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); return (process.ExitCode, await output, await error); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("系统安装包验证超时，未启动安装，可稍后重试。"); }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
        }
    }
    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken token)
    {
        var result = new System.Text.StringBuilder(); var buffer = new char[1024];
        int read; while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) if (result.Length < 8192) result.Append(buffer, 0, Math.Min(read, 8192 - result.Length));
        return result.ToString();
    }
}
