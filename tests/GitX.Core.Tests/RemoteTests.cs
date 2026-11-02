using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RemoteTests
{
    [Fact]
    public async Task Remote_head_probe_can_cancel_stalled_network_without_changing_repository()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            repo.Git("remote", "add", "origin", $"git://127.0.0.1:{port}/fixture");
            var writer = new RemoteWriter(); var context = await RepositoryActions.ReadAsync(repo.Root);
            var remote = (await writer.ReadAsync(repo.Root)).Single();
            using var cancel = new CancellationTokenSource();
            var probe = writer.RemoteHeadAsync(context, remote, "main", token: cancel.Token);
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(probe.IsCompleted); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(context.Head, repo.Git("rev-parse", "HEAD").Trim());
            Assert.Empty(repo.Git("status", "--porcelain"));
        }
        finally { listener.Stop(); }
    }
    [Fact]
    public async Task Fetch_pull_push_and_explicit_lease_protect_remote_updates()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var remotePath = Path.Combine(repo.Container, "remote.git"); repo.Git("init", "--bare", remotePath); repo.Git("remote", "add", "origin", remotePath);
        repo.Git("tag", "-a", "local-only", "-m", "must not be pushed implicitly"); repo.Git("config", "push.followTags", "true");
        var writer = new RemoteWriter(); var remote = (await writer.ReadAsync(repo.Root)).Single(); var context = await RepositoryActions.ReadAsync(repo.Root);
        await writer.ExecuteAsync(context, remote, RemoteAction.Push, "main");
        Assert.Equal(context.Head, repo.Git("--git-dir=" + remotePath, "rev-parse", "main").Trim());
        Assert.Empty(repo.Git("--git-dir=" + remotePath, "tag", "--list"));
        var lease = await writer.RemoteHeadAsync(context, remote, "main"); Assert.Equal(context.Head, lease);
        repo.Write("a", "second"); repo.Commit("second"); context = await RepositoryActions.ReadAsync(repo.Root);
        await writer.ExecuteAsync(context, remote, RemoteAction.Push, "main"); var second = context.Head;
        Assert.Equal(second, repo.Git("rev-parse", "origin/main").Trim());
        Assert.Equal("0", repo.Git("rev-list", "--count", "origin/main..HEAD").Trim());
        repo.Git("reset", "--hard", "HEAD~1"); context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, remote, RemoteAction.ForceWithLease, "main", lease));
        Assert.Equal(second, repo.Git("--git-dir=" + remotePath, "rev-parse", "main").Trim());
        await writer.ExecuteAsync(context, remote, RemoteAction.Fetch, "ignored"); context = await RepositoryActions.ReadAsync(repo.Root);
        Assert.Equal(second, repo.Git("rev-parse", "origin/main").Trim()); Assert.NotEqual(second, context.Head);
        await writer.ExecuteAsync(context, remote, RemoteAction.Pull, "main"); context = await RepositoryActions.ReadAsync(repo.Root); Assert.Equal(second, context.Head);
        repo.Git("reset", "--hard", "HEAD~1"); context = await RepositoryActions.ReadAsync(repo.Root);
        lease = await writer.RemoteHeadAsync(context, remote, "main");
        await writer.ExecuteAsync(context, remote, RemoteAction.ForceWithLease, "main", lease);
        Assert.Equal(context.Head, repo.Git("--git-dir=" + remotePath, "rev-parse", "main").Trim());
        Assert.Equal(context.Head, repo.Git("rev-parse", "origin/main").Trim());
    }

    [Fact]
    public async Task Push_to_a_different_endpoint_does_not_rewrite_fetch_tracking_ref()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var fetch = Path.Combine(repo.Container, "fetch.git"); var push = Path.Combine(repo.Container, "push.git");
        repo.Git("init", "--bare", fetch); repo.Git("init", "--bare", push); repo.Git("remote", "add", "origin", fetch);
        repo.Git("push", "-u", "origin", "main"); var tracked = repo.Git("rev-parse", "origin/main");
        repo.Git("remote", "set-url", "--push", "origin", push);
        repo.Write("a", "next"); repo.Commit("next");
        var writer = new RemoteWriter(); var remote = (await writer.ReadAsync(repo.Root)).Single();
        var config = repo.Git("config", "--local", "--list");
        await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), remote, RemoteAction.Push, "main");
        Assert.Equal(repo.Git("rev-parse", "HEAD"), repo.Git("--git-dir=" + push, "rev-parse", "main"));
        Assert.Equal(tracked, repo.Git("rev-parse", "origin/main"));
        Assert.Equal(config, repo.Git("config", "--local", "--list"));
        Assert.Equal("origin", repo.Git("remote").Trim());
    }

    [Fact]
    public async Task Push_keeps_concurrent_tracking_changes_and_reports_remote_success()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var remotePath = Path.Combine(repo.Container, "remote.git"); repo.Git("init", "--bare", remotePath); repo.Git("remote", "add", "origin", remotePath);
        repo.Git("push", "origin", "main");
        repo.Write("a", "middle"); repo.Commit("middle"); var middle = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "last"); repo.Commit("last");
        var hook = Path.Combine(repo.Root, ".git/hooks/pre-push");
        File.WriteAllText(hook, "#!/bin/sh\ngit update-ref refs/remotes/origin/main " + middle + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var writer = new RemoteWriter(); var remote = (await writer.ReadAsync(repo.Root)).Single();
        var output = await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), remote, RemoteAction.Push, "main");
        Assert.Contains("推送已成功", output);
        Assert.Equal(middle, repo.Git("rev-parse", "origin/main").Trim());
        Assert.Equal(repo.Git("rev-parse", "HEAD"), repo.Git("--git-dir=" + remotePath, "rev-parse", "main"));
    }

    [Fact]
    public async Task Pull_divergence_and_dirty_worktree_do_not_lose_local_changes()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var remotePath = Path.Combine(repo.Container, "remote.git"); repo.Git("init", "--bare", remotePath); repo.Git("remote", "add", "origin", remotePath);
        var writer = new RemoteWriter(); var remote = (await writer.ReadAsync(repo.Root)).Single();
        await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), remote, RemoteAction.Push, "main");
        repo.Write("a", "remote"); repo.Commit("remote"); await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), remote, RemoteAction.Push, "main");
        repo.Git("reset", "--hard", "HEAD~1"); repo.Write("a", "local"); repo.Commit("local"); var local = repo.Git("rev-parse", "HEAD").Trim();
        var failure = await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(RepositoryActions.ReadAsync(repo.Root).Result, remote, RemoteAction.Pull, "main"));
        Assert.Contains("Git 操作失败", failure.Message); Assert.Equal(local, repo.Git("rev-parse", "HEAD").Trim()); Assert.Empty(repo.Git("status", "--porcelain"));
        repo.Write("a", "working"); await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(RepositoryActions.ReadAsync(repo.Root).Result, remote, RemoteAction.Pull, "main"));
        Assert.Equal("working", File.ReadAllText(Path.Combine(repo.Root, "a")));
    }

    [Fact]
    public async Task Changed_remote_and_invalid_branch_are_rejected_and_credentials_redacted()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        repo.Git("remote", "add", "origin", Path.Combine(repo.Container, "missing.git")); var writer = new RemoteWriter(); var context = await RepositoryActions.ReadAsync(repo.Root); var remote = (await writer.ReadAsync(repo.Root)).Single();
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, remote, RemoteAction.Push, "invalid:name"));
        repo.Git("remote", "set-url", "origin", "different-path");
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, remote, RemoteAction.Fetch, "main"));
        Assert.Equal("https://***@example.invalid/repo", RemoteWriter.RedactUrl("https://user:secret@example.invalid/repo"));
    }
}
