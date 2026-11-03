using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GitX.Core;

internal static class AskPassProbe
{
    public static async Task RunAsync(string resultPath)
    {
        var previousTmp = Environment.GetEnvironmentVariable("TMPDIR");
        var fixture = Path.Combine(Path.GetTempPath(), "grove-askpass-test-" + Guid.NewGuid().ToString("N"));
        var longTmp = Path.Combine(fixture, new string('x', 100), new string('y', 80)); Directory.CreateDirectory(longTmp);
        var unix = !OperatingSystem.IsWindows(); bool reproduced = false;
        try
        {
            if (unix)
            {
                Environment.SetEnvironmentVariable("TMPDIR", longTmp);
                Check(Encoding.UTF8.GetByteCount(Path.GetTempPath()) > 104, "Long temporary directory was not applied.");
                try { using var baseline = new NamedPipeServerStream("gitx-askpass-" + Guid.NewGuid().ToString("N")); }
                catch (ArgumentException) { reproduced = true; }
                Check(reproduced, "Original Unix socket path failure was not reproduced.");
            }
            var prompts = 0; const string value = "fixture-value-密/+";
            var session = new GitAskPassSession(Helper(), prompt =>
            { Check(!prompt.Contains("url-secret", StringComparison.Ordinal), "Credential URL was not redacted."); prompts++; return Task.FromResult<string?>(value); }, true);
            var address = session.Authentication.Environment[GitAskPassSession.PipeVariable];
            var directory = unix ? Path.GetDirectoryName(address) : null;
            if (unix)
            {
                Check(Path.IsPathFullyQualified(address) && Encoding.UTF8.GetByteCount(address) <= 104, "Endpoint path is too long or relative.");
                Check(File.GetUnixFileMode(directory!) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Endpoint permissions are not owner-only.");
            }
            try
            {
                Check(await RequestAsync(session, "wrong", "untrusted") is null && prompts == 0, "Nonce rejection failed.");
                var response = await RequestAsync(session, Nonce(session), "https://user:url-secret@example.invalid");
                Check(JsonSerializer.Deserialize<string>(response!) == value && prompts == 1, "Credential round trip failed.");
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var client = await ClientAsync(session, "fixture credential");
                    Check(client.Code == 0 && client.Output.TrimEnd('\r', '\n') == value, "Production client Unicode round trip failed.");
                }
                Check(!session.Authentication.Sanitize(value + Uri.EscapeDataString(value)).Contains(value, StringComparison.Ordinal), "Secret redaction failed.");
                await using var other = new GitAskPassSession(Helper(), _ => throw new Exception("Isolated session was accessed."));
                Check(other.Authentication.Environment[GitAskPassSession.PipeVariable] != address, "Sessions reused an endpoint.");
                Check(await RequestAsync(other, Nonce(session), "wrong session") is null, "Session isolation failed.");
            }
            finally { await session.DisposeAsync(); }
            Check(directory is null || !Directory.Exists(directory), "Endpoint directory was not removed.");
            await using (var cancelled = new GitAskPassSession(Helper(), _ => Task.FromResult<string?>(null)))
            { Check((await ClientAsync(cancelled, "cancel")).Code != 0, "Cancellation returned a credential."); }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = new GitAskPassSession(Helper(), _ => { entered.TrySetResult(); return pending.Task; });
            var request = RequestAsync(waiting, Nonce(waiting), "pending");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await waiting.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            try { await request; } catch (IOException) { }
            pending.TrySetResult(null);
            await GitCredentialRoundTripAsync(fixture);
            var result = new { passed = true, nativeOs = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                unixLongTmpBaselineReproduced = reproduced, shortAbsoluteEndpoint = unix, privateUnixPermissions = unix,
                nonceRejected = true, unicodeClientRoundTrip = true, isolatedSessions = true, cancellation = true,
                pendingPromptDisposed = true, disposedDirectoryRemoved = true, actualGitHttpAuthentication = true };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(resultPath, json); Console.WriteLine(json);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", previousTmp);
            // Only this probe's random, verified fixture; never any repository or external endpoint.
            if (!Path.GetFullPath(fixture).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + Path.DirectorySeparatorChar + "grove-askpass-test-", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Fixture cleanup boundary failed.");
            Directory.Delete(fixture, true);
        }
    }

    private static string Helper()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "PlatformProbe.exe" : "PlatformProbe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Native probe apphost is required.");
        return executable;
    }
    private static string Nonce(GitAskPassSession session) => session.Authentication.Environment["GITX_ASKPASS_NONCE"];
    private static void Check(bool result, string error) { if (!result) throw new IOException(error); }
    private static async Task<string?> RequestAsync(GitAskPassSession session, string nonce, string prompt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = new NamedPipeClientStream(".", session.Authentication.Environment[GitAskPassSession.PipeVariable], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(nonce + "\n" + JsonSerializer.Serialize(prompt) + "\n"), timeout.Token);
        return await reader.ReadLineAsync(timeout.Token);
    }
    private static async Task<(int Code, string Output)> ClientAsync(GitAskPassSession session, string prompt)
    {
        var start = new ProcessStartInfo(Helper()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true };
        start.ArgumentList.Add(prompt); foreach (var pair in session.Authentication.Environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); return (process.ExitCode, await output); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await error; }
    }

    private static async Task GitCredentialRoundTripAsync(string fixture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; var authenticated = false; var prompts = 0;
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:fixture-password"));
        var server = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = socket.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string? line; var authorized = false;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token)))
                    if (line.StartsWith("Authorization: ", StringComparison.OrdinalIgnoreCase)) authorized = line[15..] == expected;
                authenticated |= authorized;
                var reply = authorized ? "HTTP/1.1 404 Not Found\r\n" : "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"Grove fixture\"\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "Content-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            }
        }, timeout.Token);
        await using var session = new GitAskPassSession(Helper(), prompt =>
        { prompts++; return Task.FromResult<string?>(prompt.Contains("Username", StringComparison.OrdinalIgnoreCase) ? "fixture-user" : "fixture-password"); }, true);
        var start = new ProcessStartInfo("git") { WorkingDirectory = fixture, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-c", "http.proxy=", "ls-remote", $"http://127.0.0.1:{port}/fixture.git" }) start.ArgumentList.Add(arg);
        foreach (var pair in session.Authentication.Environment) start.Environment[pair.Key] = pair.Value;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        using var git = Process.Start(start)!;
        var output = git.StandardOutput.ReadToEndAsync(); var error = git.StandardError.ReadToEndAsync();
        try { await git.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error); Check(authenticated && prompts == 2 && git.ExitCode != 0, "Actual Git askpass authentication did not reach the isolated HTTP server."); }
        finally
        {
            if (!git.HasExited) { git.Kill(true); await git.WaitForExitAsync(); }
            timeout.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { } catch (SocketException) when (timeout.IsCancellationRequested) { }
        }
    }
}
