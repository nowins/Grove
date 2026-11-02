using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GitX.Core;

// One authenticated, same-user pipe per operation. Secrets only transit memory and stdout to Git.
public sealed class GitAskPassSession : IAsyncDisposable
{
    public const string PipeVariable = "GITX_ASKPASS_PIPE";
    private readonly string _pipe = "gitx-askpass-" + Guid.NewGuid().ToString("N");
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, Task<string?>> _prompt;
    private readonly List<string> _secrets = [];
    private readonly Task _server;
    private bool _cancelled;
    public GitAuthentication Authentication { get; }

    public GitAskPassSession(string executable, Func<string, Task<string?>> prompt, bool sessionOnly = false)
    {
        _prompt = prompt;
        var helper = executable.Replace('\\', '/');
        var environment = new Dictionary<string, string> {
            ["GIT_ASKPASS"] = helper, ["SSH_ASKPASS"] = executable,
            ["SSH_ASKPASS_REQUIRE"] = "force", [PipeVariable] = _pipe, ["GITX_ASKPASS_NONCE"] = _nonce,
            ["GCM_INTERACTIVE"] = "never", ["GIT_TERMINAL_PROMPT"] = "0" };
        if (sessionOnly)
        {
            environment["GIT_CONFIG_COUNT"] = "1"; environment["GIT_CONFIG_KEY_0"] = "credential.helper"; environment["GIT_CONFIG_VALUE_0"] = "";
        }
        Authentication = new(environment, Sanitize); _server = ServeAsync();
    }
    private string Sanitize(string value)
    {
        lock (_secrets) foreach (var secret in _secrets.OrderByDescending(s => s.Length))
        { value = value.Replace(secret, "***", StringComparison.Ordinal); value = value.Replace(Uri.EscapeDataString(secret), "***", StringComparison.Ordinal); }
        return value;
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                try
                {
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    var nonce = await reader.ReadLineAsync(_stop.Token);
                    var request = await reader.ReadLineAsync(_stop.Token);
                    if (nonce != _nonce || request is null || request.Length > 16384) continue;
                    var text = JsonSerializer.Deserialize<string>(request) ?? "Git 凭据";
                    var answer = _cancelled ? null : await _prompt(RemoteWriter.RedactUrl(text));
                    if (answer is null) _cancelled = true;
                    else { lock (_secrets) if (answer.Length > 0) _secrets.Add(answer); }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(answer));
                }
                catch (IOException) when (!_stop.IsCancellationRequested) { /* A disconnected helper must not break the next request. */ }
                catch (JsonException) { /* Ignore malformed requests from other local clients. */ }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (IOException) when (_stop.IsCancellationRequested) { }
    }
    public static async Task<int> RunClientAsync(string prompt)
    {
        var pipeName = Environment.GetEnvironmentVariable(PipeVariable); var nonce = Environment.GetEnvironmentVariable("GITX_ASKPASS_NONCE");
        if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(nonce)) return 1;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(10000);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            await writer.WriteLineAsync(nonce); await writer.WriteLineAsync(JsonSerializer.Serialize(prompt));
            var answer = JsonSerializer.Deserialize<string>(await reader.ReadLineAsync() ?? "null");
            if (answer is null || answer.Contains('\n') || answer.Contains('\r')) return 1;
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            await output.WriteLineAsync(answer); return 0;
        }
        catch (IOException) { return 1; }
        catch (TimeoutException) { return 1; }
        catch (JsonException) { return 1; }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _server; _stop.Dispose(); lock (_secrets) _secrets.Clear();
    }
}
