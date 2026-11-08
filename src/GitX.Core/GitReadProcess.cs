using System.Diagnostics;
using System.Text;

namespace GitX.Core;

// Only read commands are exposed. Cancellation must never be reused for future Git writes.
internal static class GitReadProcess
{
    public const int MaxOutputCharacters = 2 * 1024 * 1024;

    public static async Task<string> RunAsync(string root, IEnumerable<string> arguments, CancellationToken cancellation, bool allowDifferences = false, bool allowMergeConflicts = false,
        IReadOnlyDictionary<string, string>? environment = null, Func<string, string>? sanitize = null, TimeSpan? readTimeout = null, Encoding? outputEncoding = null,
        ReadPriority priority = ReadPriority.Foreground, Func<Task>? afterExit = null)
    {
        ReadScheduler.Lease? lease = null;
        var handedOff = false;
        try
        {
            var capturedArguments = arguments.ToArray();
            var capturedEnvironment = environment?.ToDictionary(pair => pair.Key, pair => pair.Value);
            lease = await ReadWork.Processes.AcquireAsync(cancellation, priority).ConfigureAwait(false);
            var owner = lease;
            var running = Task.Run(() => RunOwnedAsync(root, capturedArguments, cancellation, allowDifferences, allowMergeConflicts,
                capturedEnvironment, sanitize, readTimeout, outputEncoding, owner, afterExit));
            handedOff = true;
            return await running.ConfigureAwait(false);
        }
        finally
        {
            if (!handedOff)
                await ReleaseAsync(null, lease, afterExit).ConfigureAwait(false);
        }
    }

    private static async Task<string> RunOwnedAsync(string root, string[] arguments, CancellationToken cancellation, bool allowDifferences, bool allowMergeConflicts,
        IReadOnlyDictionary<string, string>? environment, Func<string, string>? sanitize, TimeSpan? readTimeout, Encoding? outputEncoding,
        ReadScheduler.Lease lease, Func<Task>? afterExit)
    {
        Process? process = null;
        Task? exited = null;
        try
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
            process = GitProcess.Start(start);
            // Never wait for exit with the already-cancelled read token. Process
            // creation/termination also runs here, off the caller's UI thread.
            exited = process.WaitForExitAsync(CancellationToken.None);
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token, overflow);
            var errors = ReadBoundedAsync(process.StandardError, timeout.Token, overflow);
            try
            {
                await Task.WhenAll(output, errors, exited.WaitAsync(timeout.Token)).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 && !(allowDifferences && process.ExitCode == 1) && !(allowMergeConflicts && process.ExitCode is > 0 and < 128))
                    throw new IOException(Clean($"Git 读取失败 ({process.ExitCode})：{await errors}"));
                return Clean(await output);
            }
            catch (Exception error)
            {
                var failure = await StopAsync(() => { if (!process.HasExited) process.Kill(entireProcessTree: true); }, exited,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (!exited.IsCompletedSuccessfully)
                    throw new IOException(Clean($"Git 读取未结束（PID {process.Id}），已保留执行额度及临时输入，等待实际退出。{failure}"), error);
                if (error is OperationCanceledException && !cancellation.IsCancellationRequested)
                    throw new IOException(Clean((overflow.IsCancellationRequested
                        ? $"Git 读取输出超过 {MaxOutputCharacters:N0} 字符，请缩小查看范围。"
                        : $"Git 读取超过 {duration.TotalSeconds:g} 秒，请检查仓库或网络后重试。") + failure), error);
                throw;
            }
            string Clean(string value) => sanitize?.Invoke(value) ?? value;
        }
        finally
        {
            if (exited is { IsCompletedSuccessfully: false }) _ = ReleaseAfterExitAsync(process!, exited, lease, afterExit);
            else await ReleaseAsync(process, lease, afterExit).ConfigureAwait(false);
        }
    }

    internal static async Task<string?> StopAsync(Action terminate, Task exited, TimeSpan grace)
    {
        string? failure = null;
        try { terminate(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        { failure = " 终止读取时遇到错误：" + e.Message; }
        try { await exited.WaitAsync(grace).ConfigureAwait(false); }
        catch (TimeoutException) { failure += " 等待读取进程退出超时。"; }
        return failure;
    }

    private static async Task ReleaseAfterExitAsync(Process process, Task exited, ReadScheduler.Lease lease, Func<Task>? afterExit)
    {
        try { await exited.ConfigureAwait(false); }
        catch (Exception e)
        {
            Trace.TraceWarning("Git 读取退出通知失败：{0}", e.GetType().Name);
            // Keep the same process handle and lease; never act on a recycled PID.
            while (true)
            {
                try { if (process.HasExited) break; }
                catch (Exception retry) when (retry is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
        await ReleaseAsync(process, lease, afterExit).ConfigureAwait(false);
    }

    private static async Task ReleaseAsync(Process? process, ReadScheduler.Lease? lease, Func<Task>? afterExit)
    {
        try
        {
            process?.Dispose();
            if (afterExit is not null) await afterExit().ConfigureAwait(false);
        }
        catch (Exception e) { Trace.TraceWarning("Git 读取资源释放失败：{0}", e.GetType().Name); }
        finally { lease?.Dispose(); }
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
