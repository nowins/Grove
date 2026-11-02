using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GitX.Core;
using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public void Missing_git_reports_install_and_path_action()
    {
        using var repo = new TestRepository();
        var error = Assert.Throws<IOException>(() => GitProcess.Start(new ProcessStartInfo(Path.Combine(repo.Root, "missing-git.exe")) { WorkingDirectory = repo.Root, UseShellExecute = false }));
        Assert.Contains("找不到 Git", error.Message); Assert.Contains("PATH", error.Message);
    }

    [Fact]
    public async Task Read_limit_and_deadline_are_distinct_and_caller_cancel_stays_cancellation()
    {
        using var repo = new TestRepository(); repo.Write("huge", new string('a', GitReadProcess.MaxOutputCharacters + 100)); repo.Commit("base");
        var limit = await Assert.ThrowsAsync<IOException>(() => GitReadProcess.RunAsync(repo.Root, ["show", "HEAD:huge"], default));
        Assert.Contains("字符", limit.Message); Assert.DoesNotContain("秒", limit.Message);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var read = GitReadProcess.RunAsync(repo.Root, ["ls-remote", $"git://127.0.0.1:{port}/fixture"], default, readTimeout: TimeSpan.FromSeconds(1));
        using var peer = await listener.AcceptTcpClientAsync();
        var timeout = await Assert.ThrowsAsync<IOException>(() => read);
        Assert.Contains("超过 1 秒", timeout.Message); Assert.DoesNotContain("字符", timeout.Message);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitReadProcess.RunAsync(repo.Root, ["status", "--porcelain"], cancel.Token));
    }

    [Fact]
    public async Task Write_output_truncation_is_visible_without_turning_success_into_failure()
    {
        using var repo = new TestRepository();
        repo.Write("huge", string.Concat(Enumerable.Repeat("output line\n", 200000))); repo.Commit("base");
        var output = await GitWriteProcess.RunAsync(repo.Root, ["show", "HEAD:huge"]);
        Assert.Contains("输出已截断", output); Assert.StartsWith("output line\n", output);
        Assert.InRange(output.Length, 1, GitReadProcess.MaxOutputCharacters + 200);
        Assert.Equal("base", repo.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Editor_port_collision_retries_and_preserves_capability_boundary()
    {
        var occupiedPort = Port(); using var occupied = new HttpListener();
        occupied.Prefixes.Add($"http://127.0.0.1:{occupiedPort}/"); occupied.Start();
        var attempts = 0;
        using var server = new EditorServer("index.html", () => ++attempts == 1 ? occupiedPort : Port());
        Assert.Equal(2, attempts);
        using var client = new HttpClient();
        Assert.Equal("{}", await client.GetStringAsync(new Uri(server.Address, "document")));
        var denied = await client.GetAsync(new Uri(server.Address.GetLeftPart(UriPartial.Authority) + "/document"));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        attempts = 0;
        var error = Assert.Throws<IOException>(() => new EditorServer("index.html", () => { attempts++; return occupiedPort; }));
        Assert.Equal(5, attempts); Assert.Contains("5 个端口", error.Message);
    }
    private static int Port()
    { using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); return ((IPEndPoint)probe.LocalEndpoint).Port; }
}
