using System.Diagnostics;
using System.Text;

namespace GitX.Core;

// No read-request cancellation or timeout. Only a user-confirmed write scope
// can terminate its currently attached process.
internal static class GitWriteProcess
{
    public static async Task<string> RunAsync(string root, IEnumerable<string> arguments, byte[]? input = null, bool noEditor = false, bool literalPaths = true, bool includeErrorOutput = false,
        IReadOnlyDictionary<string, string>? environment = null, Func<string, string>? sanitize = null, string? operationCommand = null)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (literalPaths) start.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        else start.Environment.Remove("GIT_LITERAL_PATHSPECS");
        if (noEditor) { start.Environment["GIT_EDITOR"] = "true"; start.Environment["GIT_SEQUENCE_EDITOR"] = "true"; }
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.quotepath=false");
        var args = arguments.ToArray();
        foreach (var argument in args) start.ArgumentList.Add(argument);
        var operation = GitWriteOperation.Current;
        operation?.ThrowIfTerminated();
        using var process = GitProcess.Start(start);
        operation?.Attach(process, operationCommand ?? args.FirstOrDefault() ?? "");
        try
        {
            var output = DrainAsync(process.StandardOutput, text => operation?.Append(Clean(text)));
            var errors = DrainAsync(process.StandardError, text => operation?.Append(Clean(text)));
            Exception? inputError = null;
            try { if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input); }
            catch (IOException e) { inputError = e; }
            finally { process.StandardInput.Close(); }
            await Task.WhenAll(output, errors, process.WaitForExitAsync());
            operation?.ThrowIfTerminated();
            var stdout = await output; var stderr = await errors;
            var notice = stdout.Truncated || stderr.Truncated ? "\n[Git 输出已截断：每个输出流最多保留 2,097,152 字符；命令已等待结束，结果由退出状态判断。]\n" : "";
            operation?.Append(notice);
            if (process.ExitCode != 0) throw new IOException(Clean($"Git 操作失败 ({process.ExitCode})：{stderr.Text}\n{stdout.Text}{notice}"));
            if (inputError is not null) throw inputError;
            return Clean((includeErrorOutput ? stdout.Text + stderr.Text : stdout.Text) + notice);
        }
        catch { operation?.ThrowIfTerminated(); throw; }
        finally { operation?.Detach(process); }
        string Clean(string value) => sanitize?.Invoke(value) ?? value;
    }

    // Repository-wide commands can internally construct magic pathspecs (notably stash -u).
    // Literal path mode is only appropriate for commands that receive our file paths.
    public static Task<string> RepositoryAsync(string root, IEnumerable<string> arguments, byte[]? input = null, bool noEditor = true)
        => RunAsync(root, arguments, input, noEditor, literalPaths: false, includeErrorOutput: true);

    private sealed record Output(string Text, bool Truncated);
    private static async Task<Output> DrainAsync(StreamReader reader, Action<string> publish)
    {
        var result = new StringBuilder(); var pending = new StringBuilder(); var buffer = new char[8192]; int count; var omitted = false; var truncated = false;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var remaining = 2 * 1024 * 1024 - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
            // Publish complete records only, so chunk boundaries cannot split
            // a credential before the operation's sanitizer sees it.
            var ready = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                if (!omitted && pending.Length < 64 * 1024) pending.Append(buffer[i]);
                else omitted = true;
                if (buffer[i] is '\n' or '\r') { ready.Append(omitted ? "[超长输出行已省略]\n" : pending.ToString()); pending.Clear(); omitted = false; }
            }
            if (ready.Length > 0) publish(ready.ToString());
        }
        if (pending.Length > 0) publish(omitted ? "[超长输出行已省略]\n" : pending.ToString());
        var text = result.ToString();
        // Do not return a partial record cut through a credential at the cap.
        if (truncated) text = text[..(text.LastIndexOfAny(['\n', '\r']) + 1)];
        return new(text, truncated);
    }
}
