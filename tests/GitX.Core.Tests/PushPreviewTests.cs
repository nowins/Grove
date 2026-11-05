using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class PushPreviewTests
{
    private static async Task<(RemoteEntry Remote, string Path)> RemoteAsync(TestRepository repo)
    {
        var path = Path.Combine(repo.Container, "push target.git");
        repo.Git("init", "--bare", path); repo.Git("remote", "add", "origin", path);
        return ((await new RemoteWriter().ReadAsync(repo.Root)).Single(), path);
    }
    private static Task<PushPreview> Read(TestRepository repo, RemoteEntry remote, string branch = "main")
        => ReadCore(repo, remote, branch);
    private static async Task<PushPreview> ReadCore(TestRepository repo, RemoteEntry remote, string branch)
        => await new PushPreviewReader().ReadAsync(await RepositoryActions.ReadAsync(repo.Root), remote, branch);

    [Fact]
    public async Task New_branch_and_synchronized_are_distinct_and_read_is_nonmutating()
    {
        using var repo = new TestRepository(); repo.Write("中文 file.txt", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); var reader = new PushPreviewReader();
        var before = await RepositoryActions.ReadAsync(repo.Root);
        var preview = await Read(repo, remote);
        Assert.Equal(PushRelation.NewBranch, preview.Relation); Assert.Equal(1, preview.CommitCount);
        var commits = await reader.CommitsAsync(preview); Assert.Single(commits);
        Assert.Equal(new PushFile("A", "中文 file.txt"), Assert.Single(await reader.FilesAsync(preview, commits[0])));
        Assert.Equal(before.Token, (await RepositoryActions.ReadAsync(repo.Root)).Token);
        await reader.ExecuteAsync(preview, false);
        Assert.Equal(before.Head, repo.Git("--git-dir=" + path, "rev-parse", "main").Trim());
        preview = await Read(repo, remote);
        Assert.Equal(PushRelation.Synchronized, preview.Relation); Assert.False(preview.CanPush); Assert.Equal(0, preview.CommitCount);
        await Assert.ThrowsAsync<IOException>(() => reader.ExecuteAsync(preview, false));
    }

    [Fact]
    public async Task Ahead_lists_only_outgoing_commits_and_changed_files()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); repo.Git("push", "origin", "main");
        repo.Write("a", "next"); repo.Write("b", "new"); repo.Commit("next");
        repo.Write("uncommitted", "keep");
        var before = await RepositoryActions.ReadAsync(repo.Root); var reader = new PushPreviewReader(); var p = await Read(repo, remote);
        Assert.Equal(PushRelation.Ahead, p.Relation); Assert.Equal(1, p.CommitCount);
        var files = await reader.FilesAsync(p, Assert.Single(await reader.CommitsAsync(p)));
        Assert.Contains(new PushFile("M", "a"), files); Assert.Contains(new PushFile("A", "b"), files);
        await reader.ExecuteAsync(p, false);
        Assert.Equal(before.Head, repo.Git("rev-parse", "HEAD").Trim()); Assert.Equal("keep", File.ReadAllText(Path.Combine(repo.Root, "uncommitted")));
        Assert.Equal(before.Head, repo.Git("--git-dir=" + path, "rev-parse", "main").Trim());
    }

    [Theory]
    [InlineData("head")]
    [InlineData("config")]
    [InlineData("branch")]
    public async Task Stale_local_snapshot_is_rejected(string change)
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); var p = await Read(repo, remote);
        if (change == "head") { repo.Write("a", "next"); repo.Commit("next"); }
        else if (change == "config") repo.Git("remote", "set-url", "--push", "origin", path + ".changed");
        else repo.Git("switch", "-c", "different");
        await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().ExecuteAsync(p, false));
        Assert.Empty(repo.Git("--git-dir=" + path, "for-each-ref", "refs/heads"));
    }

    [Fact]
    public async Task Remote_change_since_review_rejected_without_touching_local_files()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); var p = await Read(repo, remote);
        // Fetch from the bare target: pushing to its URL can also update the
        // source's origin/main on newer Git, masking the remote-only check.
        repo.Git("--git-dir=" + path, "fetch", repo.Root, "HEAD:refs/heads/main");
        var before = await RepositoryActions.ReadAsync(repo.Root);
        Assert.Equal(p.Context.Token, before.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().ExecuteAsync(p, false));
        Assert.Contains("远程目标已变化", error.Message);
        Assert.Equal(before.Token, (await RepositoryActions.ReadAsync(repo.Root)).Token);
    }

    [Fact]
    public async Task Behind_and_diverged_require_explicit_force_and_keep_lease()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); var basis = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "remote"); repo.Commit("remote"); repo.Git("push", "origin", "main");
        repo.Git("reset", "--hard", basis); var p = await Read(repo, remote);
        Assert.Equal(PushRelation.Behind, p.Relation); Assert.Equal(0, p.CommitCount);
        await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().ExecuteAsync(p, false));
        repo.Write("a", "local"); repo.Commit("local"); p = await Read(repo, remote);
        Assert.Equal(PushRelation.Diverged, p.Relation); Assert.Equal(1, p.CommitCount);
        await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().ExecuteAsync(p, false));
        await new PushPreviewReader().ExecuteAsync(p, true);
        Assert.Equal(p.Context.Head, repo.Git("--git-dir=" + path, "rev-parse", "main").Trim());
    }

    [Fact]
    public async Task Unknown_remote_object_is_not_zero_and_does_not_fetch()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo);
        using var other = new TestRepository(); other.Write("other", "remote"); other.Commit("remote"); other.Git("push", path, "main");
        var before = await RepositoryActions.ReadAsync(repo.Root); var p = await Read(repo, remote);
        Assert.Equal(PushRelation.Unknown, p.Relation); Assert.Null(p.CommitCount); Assert.False(p.CanPush);
        await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().CommitsAsync(p));
        await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().ExecuteAsync(p, true));
        Assert.Equal(before.Token, (await RepositoryActions.ReadAsync(repo.Root)).Token);
    }

    [Fact]
    public async Task Distinct_push_endpoint_and_named_target_are_used()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, fetch) = await RemoteAsync(repo); repo.Git("push", "-u", "origin", "main");
        var push = Path.Combine(repo.Container, "other.git"); repo.Git("init", "--bare", push); repo.Git("remote", "set-url", "--push", "origin", push);
        remote = (await new RemoteWriter().ReadAsync(repo.Root)).Single();
        var p = await Read(repo, remote, "release/中文"); Assert.Equal(PushRelation.NewBranch, p.Relation);
        await new PushPreviewReader().ExecuteAsync(p, false);
        Assert.Equal(p.Context.Head, repo.Git("--git-dir=" + push, "rev-parse", "release/中文").Trim());
        Assert.Equal("refs/heads/main", repo.Git("config", "branch.main.merge").Trim());
        Assert.Equal(p.Context.Head, repo.Git("--git-dir=" + fetch, "rev-parse", "main").Trim());
    }

    [Fact]
    public async Task Commit_pages_are_bounded_and_disjoint()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, _) = await RemoteAsync(repo);
        for (var i = 0; i < 103; i++) repo.Git("commit", "--allow-empty", "-m", "page " + i);
        var p = await Read(repo, remote); var reader = new PushPreviewReader();
        var first = await reader.CommitsAsync(p); var second = await reader.CommitsAsync(p, 100);
        Assert.Equal(104, p.CommitCount); Assert.Equal(100, first.Count); Assert.Equal(4, second.Count);
        Assert.Empty(first.Select(c => c.Sha).Intersect(second.Select(c => c.Sha)));
    }

    [Fact]
    public async Task Failed_push_does_not_change_refs_index_or_worktree()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); repo.Git("--git-dir=" + path, "config", "receive.denyNonFastForwards", "true");
        repo.Git("push", "origin", "main"); var basis = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "remote"); repo.Commit("remote"); repo.Git("push", "origin", "main");
        var remoteHead = repo.Git("--git-dir=" + path, "rev-parse", "main"); repo.Git("reset", "--hard", basis);
        var before = await RepositoryActions.ReadAsync(repo.Root); var p = await Read(repo, remote);
        await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().ExecuteAsync(p, true));
        Assert.Equal(remoteHead, repo.Git("--git-dir=" + path, "rev-parse", "main"));
        Assert.Equal(before.Token, (await RepositoryActions.ReadAsync(repo.Root)).Token);
    }
    [Fact]
    public async Task Repeated_execution_cannot_send_twice_from_one_approval()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); var p = await Read(repo, remote);
        var reader = new PushPreviewReader(); await reader.ExecuteAsync(p, false);
        await Assert.ThrowsAsync<IOException>(() => reader.ExecuteAsync(p, false));
        Assert.Equal(p.Context.Head, repo.Git("--git-dir=" + path, "rev-parse", "main").Trim());
    }

    [Fact]
    public async Task Reviewed_normal_push_cannot_bypass_fast_forward_even_with_forged_preview()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); var basis = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "remote"); repo.Commit("remote"); repo.Git("push", "origin", "main");
        var remoteHead = repo.Git("--git-dir=" + path, "rev-parse", "main"); repo.Git("reset", "--hard", basis);
        var p = await Read(repo, remote);
        await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().ExecuteAsync(p with { Relation = PushRelation.Ahead }, false));
        Assert.Equal(remoteHead, repo.Git("--git-dir=" + path, "rev-parse", "main"));
    }

    [Fact]
    public async Task Remote_update_after_probe_is_rejected_by_server_lease()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo); repo.Git("push", "origin", "main");
        var basis = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "next"); repo.Commit("next"); var p = await Read(repo, remote);
        var hook = Path.Combine(repo.Root, ".git/hooks/pre-push");
        // Deletion races after ls-remote and the push advertisement. A normal
        // reviewed push must not silently recreate the deleted branch.
        File.WriteAllText(hook, "#!/bin/sh\ngit --git-dir='" + path.Replace("'", "'\\''") + "' update-ref -d refs/heads/main " + basis + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.ThrowsAsync<IOException>(() => new PushPreviewReader().ExecuteAsync(p, false));
        Assert.Empty(repo.Git("--git-dir=" + path, "for-each-ref", "refs/heads"));
        Assert.Equal(p.Context.Head, repo.Git("rev-parse", "HEAD").Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Url_rewrites_bind_review_and_push_to_the_same_endpoint(bool explicitPushUrl)
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, fetch) = await RemoteAsync(repo); repo.Git("push", "origin", "main");
        var tracked = repo.Git("rev-parse", "origin/main");
        var push = Path.Combine(repo.Container, "actual-push.git"); repo.Git("init", "--bare", push);
        repo.Git("config", "url." + push + ".pushInsteadOf", fetch);
        if (explicitPushUrl) repo.Git("remote", "set-url", "--push", "origin", fetch);
        remote = (await new RemoteWriter().ReadAsync(repo.Root)).Single();
        repo.Write("a", "local"); repo.Commit("local");
        var p = await Read(repo, remote);
        Assert.Equal(explicitPushUrl ? fetch : push, p.PushUrl);
        Assert.Equal(explicitPushUrl ? PushRelation.Ahead : PushRelation.NewBranch, p.Relation);
        await new PushPreviewReader().ExecuteAsync(p, false);
        Assert.Equal(p.Context.Head, repo.Git("--git-dir=" + p.PushUrl, "rev-parse", "main").Trim());
        if (!explicitPushUrl)
        {
            Assert.Equal(tracked, repo.Git("rev-parse", "origin/main"));
            Assert.Equal(tracked, repo.Git("--git-dir=" + fetch, "rev-parse", "main"));
        }
        else Assert.Empty(repo.Git("--git-dir=" + push, "for-each-ref", "refs/heads"));
    }

    [Fact]
    public async Task InsteadOf_is_resolved_once_and_cascading_rewrites_are_rejected()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, actual) = await RemoteAsync(repo);
        repo.Git("remote", "set-url", "origin", "grove-alias:repo");
        repo.Git("config", "url." + actual + ".insteadOf", "grove-alias:repo");
        remote = (await new RemoteWriter().ReadAsync(repo.Root)).Single();
        var p = await Read(repo, remote); Assert.Equal(actual, p.PushUrl);
        await new PushPreviewReader().ExecuteAsync(p, false);
        Assert.Equal(p.Context.Head, repo.Git("--git-dir=" + actual, "rev-parse", "main").Trim());
        repo.Git("config", "url." + actual + ".insteadOf", "grove-mid:repo");
        repo.Git("config", "url.grove-mid:repo.insteadOf", "grove-alias:repo");
        remote = (await new RemoteWriter().ReadAsync(repo.Root)).Single();
        var error = await Assert.ThrowsAsync<IOException>(() => Read(repo, remote));
        Assert.Contains("级联", error.Message);
    }

    [Theory]
    [InlineData("https://user:secret@example.invalid/repo?token=private#hidden")]
    [InlineData("ssh://user:secret@example.invalid/repo?token=private#hidden")]
    [InlineData("https://example.invalid/repo?token=private#hidden")]
    public void Endpoint_display_and_errors_remove_url_secrets(string url)
    {
        var text = RemoteWriter.RedactUrl("failed: " + url);
        Assert.DoesNotContain("secret", text); Assert.DoesNotContain("private", text); Assert.DoesNotContain("hidden", text);
        Assert.Contains("example.invalid/repo", text);
    }

    [Fact]
    public async Task Cancelled_commit_and_file_reads_do_not_change_repository()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, _) = await RemoteAsync(repo); var p = await Read(repo, remote); var reader = new PushPreviewReader();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.CommitsAsync(p, 100, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.FilesAsync(p, new(p.Context.Head!, "base"), cancellation.Token));
        Assert.Equal(p.Context.Token, (await RepositoryActions.ReadAsync(repo.Root)).Token);
    }

    [Fact]
    public async Task Reviewed_push_termination_targets_only_the_captured_process()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var (remote, path) = await RemoteAsync(repo);
        repo.Write(".git/hooks/pre-push", "#!/bin/sh\necho 'push hook ready' >&2\nwhile [ ! -f release-hook ]; do sleep 0.1; done\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(repo.Root, ".git/hooks/pre-push"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var p = await Read(repo, remote);
        using var model = new GitX.Desktop.ViewModels.MainViewModel(); await model.OpenAsync(repo.Root);
        var write = model.ExecuteActionAsync(repo.Root, () => new PushPreviewReader().ExecuteAsync(p, false), "reviewed push");
        try
        {
            for (var i = 0; i < 200 && model.LastGitOperation?.Snapshot().Output.Contains("push hook ready") != true; i++) await Task.Delay(25);
            var operation = model.LastGitOperation!;
            Assert.Contains("push hook ready", operation.Snapshot().Output);
            var pid = operation.Snapshot().ProcessId!.Value;
            Assert.False(operation.Terminate(pid + 1)); Assert.True(model.IsWriting);
            Assert.True(operation.Terminate(pid)); await write.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(model.HasError); Assert.Contains("可能已部分完成", model.Error);
            Assert.False(operation.Terminate(pid)); Assert.False(model.IsWriting);
            Assert.Empty(repo.Git("--git-dir=" + path, "for-each-ref", "refs/heads"));
            Assert.Equal(p.Context.Head, repo.Git("rev-parse", "HEAD").Trim());
        }
        finally { repo.Write("release-hook", "done"); await write.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task File_details_preserve_literal_paths_and_show_add_modify_delete()
    {
        using var repo = new TestRepository(); repo.Write("old file", "base"); repo.Write("changed", "base"); repo.Commit("base");
        var (remote, _) = await RemoteAsync(repo); repo.Git("push", "origin", "main");
        repo.Git("rm", "old file"); repo.Write("changed", "next"); repo.Write("中文 file", "new"); repo.Commit("changes");
        var p = await Read(repo, remote); var reader = new PushPreviewReader();
        var files = await reader.FilesAsync(p, Assert.Single(await reader.CommitsAsync(p)));
        Assert.Contains(new PushFile("A", "中文 file"), files);
        Assert.Contains(new PushFile("M", "changed"), files);
        Assert.Contains(new PushFile("D", "old file"), files);
    }

}
