using System.Diagnostics;
using System.Text;

namespace GitX.Core;

// Only read commands are exposed. Cancellation must never be reused for future Git writes.
internal static class GitReadProcess
{
    public const int MaxOutputCharacters = 2 * 1024 * 1024;

    public static async Task<string> RunAsync(string root, IEnumerable<string> arguments, CancellationToken cancellation, bool allowDifferences = false, bool allowMergeConflicts = false,
        IReadOnlyDictionary<string, string>? environment = null, Func<string, string>? sanitize = null, TimeSpan? readTimeout = null, Encoding? outputEncoding = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var duration = readTimeout ?? TimeSpan.FromSeconds(30);
        using var deadline = new CancellationTokenSource(duration);
        using var overflow = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token, overflow.Token);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = GitProcess.Start(start);
        string? killFailure = null;
        using var registration = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception e) { killFailure = $" 未能停止读取进程（PID {process.Id}）：{e.Message}"; }
        });
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token, overflow);
        var errors = ReadBoundedAsync(process.StandardError, timeout.Token, overflow);
        try
        {
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0 && !(allowDifferences && process.ExitCode == 1) && !(allowMergeConflicts && process.ExitCode is > 0 and < 128))
                throw new IOException(Clean($"Git 读取失败 ({process.ExitCode})：{await errors}"));
            return Clean(await output);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException((overflow.IsCancellationRequested
                ? $"Git 读取输出超过 {MaxOutputCharacters:N0} 字符，请缩小查看范围。"
                : $"Git 读取超过 {duration.TotalSeconds:g} 秒，请检查仓库或网络后重试。") + killFailure);
        }
        string Clean(string value) => sanitize?.Invoke(value) ?? value;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellation, CancellationTokenSource overflow)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
        {
            if (result.Length + count > MaxOutputCharacters)
            {
                overflow.Cancel();
                throw new OperationCanceledException(cancellation);
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
