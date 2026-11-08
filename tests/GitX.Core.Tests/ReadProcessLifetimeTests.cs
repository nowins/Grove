using System.Net;
using System.Net.Sockets;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ReadProcessLifetimeTests
{
    [Fact]
    public async Task Tree_termination_failure_is_reported_and_still_waits_for_actual_exit()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = GitReadProcess.StopAsync(() => throw new AggregateException(new System.ComponentModel.Win32Exception(5)), exited.Task, TimeSpan.FromSeconds(5));
        Assert.False(stopped.IsCompleted);
        exited.SetResult(); Assert.Contains("终止读取", await stopped);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = await GitReadProcess.StopAsync(() => { }, pending.Task, TimeSpan.FromMilliseconds(10));
        Assert.Contains("退出超时", failure); Assert.False(pending.Task.IsCompleted);
        pending.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_deadline_closes_the_read_connection_before_cleaning_inputs(bool deadline)
    {
        var inputs = new TemporaryGitInputs("diff", "old.txt", "new.txt");
        await File.WriteAllTextAsync(Path.Combine(inputs.DirectoryPath, "old.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(inputs.DirectoryPath, "new.txt"), "new");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource();
        var cleaned = 0;
        var read = GitReadProcess.RunAsync(inputs.DirectoryPath, ["ls-remote", $"git://127.0.0.1:{port}/fixture"], cancellation.Token,
            readTimeout: TimeSpan.FromSeconds(deadline ? 2 : 30), afterExit: async () => { Interlocked.Increment(ref cleaned); await inputs.CleanupAsync(); });
        try
        {
            using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var stream = peer.GetStream(); var bytes = new byte[4096];
            Assert.True(await stream.ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) > 0);
            if (deadline) Assert.Contains("超过 2 秒", (await Assert.ThrowsAsync<IOException>(() => read)).Message);
            else { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read); }
            try { Assert.Equal(0, await stream.ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5))); }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
            { /* Terminating a connected process can close TCP with RST rather than FIN. */ }
            Assert.Equal(1, cleaned); Assert.False(Directory.Exists(inputs.DirectoryPath));
        }
        finally
        {
            cancellation.Cancel();
            try { await read.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception e) when (e is OperationCanceledException or IOException) { }
            await inputs.CleanupAsync();
        }
    }

    [Fact]
    public async Task Cancellation_before_start_and_start_failure_release_owned_inputs_once()
    {
        foreach (var cancel in new[] { true, false })
        {
            var inputs = new TemporaryGitInputs("diff", "old.txt");
            await File.WriteAllTextAsync(Path.Combine(inputs.DirectoryPath, "old.txt"), "private input");
            var cleaned = 0; using var cancellation = new CancellationTokenSource();
            if (cancel) cancellation.Cancel();
            var read = GitReadProcess.RunAsync(cancel ? inputs.DirectoryPath : Path.Combine(inputs.DirectoryPath, "missing"), ["--version"], cancellation.Token,
                afterExit: async () => { cleaned++; await inputs.CleanupAsync(); });
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            else await Assert.ThrowsAsync<IOException>(() => read);
            Assert.Equal(1, cleaned); Assert.False(Directory.Exists(inputs.DirectoryPath));
        }
    }

    [Fact]
    public async Task Cleanup_failure_cannot_replace_the_read_error_or_cancellation()
    {
        using var repo = new TestRepository();
        var error = await Assert.ThrowsAsync<IOException>(() => GitReadProcess.RunAsync(repo.Root, ["grove-missing-command"], default,
            afterExit: () => throw new IOException("cleanup failure")));
        Assert.Contains("Git 读取失败", error.Message); Assert.DoesNotContain("cleanup failure", error.Message);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitReadProcess.RunAsync(repo.Root, ["--version"], cancellation.Token,
            afterExit: () => throw new IOException("cleanup failure")));
    }

    [Fact]
    public async Task Temporary_inputs_retry_transient_file_handles_without_recursive_deletion()
    {
        var inputs = new TemporaryGitInputs("merge", "base", "left", "right");
        var file = Path.Combine(inputs.DirectoryPath, "base"); await File.WriteAllTextAsync(file, "text");
        var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var cleanup = inputs.CleanupAsync();
        try { if (OperatingSystem.IsWindows()) Assert.False(cleanup.IsCompleted); }
        finally { held.Dispose(); }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(inputs.DirectoryPath));
    }
}
