using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class GitWriteOperationTests
{
    [Fact]
    public async Task Live_remote_output_uses_the_same_secret_sanitizer_as_final_output()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var remotePath = Path.Combine(repo.Container, "origin.git"); repo.Git("init", "--bare", remotePath);
        repo.Git("remote", "add", "origin", remotePath);
        repo.Write(".git/hooks/pre-push", "#!/bin/sh\nprintf 'fixture-' >&2\nsleep 0.1\nprintf 'token\\n' >&2\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(repo.Root, ".git/hooks/pre-push"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var writer = new RemoteWriter(); var context = await RepositoryActions.ReadAsync(repo.Root);
        var remote = (await writer.ReadAsync(repo.Root)).Single(); var operation = new GitWriteOperation(repo.Root, "test push");
        var authentication = new GitAuthentication(new Dictionary<string, string>(), text => text.Replace("fixture-token", "***"));
        await operation.RunAsync(async () => { await writer.ExecuteAsync(context, remote, RemoteAction.Push, "main", authentication: authentication); });
        operation.Complete(null);
        Assert.DoesNotContain("fixture-token", operation.Snapshot().Output);
        Assert.Contains("***", operation.Snapshot().Output);
    }
    [Fact]
    public async Task Slow_hook_can_finish_in_background_and_refreshes_actual_head()
    {
        using var repo = Fixture(); using var model = new MainViewModel();
        await model.OpenAsync(repo.Root); model.SwitchView(1); model.ToggleIncludeFile("a.txt"); var before = repo.Git("rev-parse", "HEAD").Trim(); model.CommitMessage = "completed hook";
        var write = model.CommitAsync();
        try
        {
            await Until(() => model.LastGitOperation?.Snapshot().Output.Contains("hook ready") == true);
            Assert.True(model.IsWriting); Assert.False(model.CanCommit);
            model.SwitchView(0); model.SelectedEntry = model.Entries.First();
            await Until(() => model.CurrentDocument?.Path == "a.txt");
            Assert.Equal("changed\n", model.CurrentDocument!.Text);
            Assert.Equal(before, repo.Git("rev-parse", "HEAD").Trim());
        }
        finally { repo.Write("release-hook", "go"); await write.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.False(model.IsWriting); Assert.False(model.LastGitOperation!.Snapshot().Running);
        Assert.False(model.HasError); Assert.Empty(model.CommitMessage);
        Assert.NotEqual(before, repo.Git("rev-parse", "HEAD").Trim());
        Assert.Contains("hook done", model.LastGitOperation.Snapshot().Output);
        Assert.False(model.LastGitOperation.Terminate(-1));
    }

    [Fact]
    public async Task Explicit_termination_retains_message_staged_content_and_reconciles_once()
    {
        using var repo = Fixture(); using var model = new MainViewModel();
        await model.OpenAsync(repo.Root); model.SwitchView(1); model.ToggleIncludeFile("a.txt"); var before = repo.Git("rev-parse", "HEAD").Trim(); model.CommitMessage = "keep me";
        var write = model.CommitAsync();
        try
        {
            await Until(() => model.LastGitOperation?.Snapshot().Output.Contains("hook ready") == true);
            var operation = model.LastGitOperation!; var pid = operation.Snapshot().ProcessId!.Value;
            Assert.False(operation.Terminate(pid + 1)); Assert.True(model.IsWriting);
            Assert.True(operation.Terminate(pid));
            await write.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(model.HasError); Assert.Contains("可能已部分完成", model.Error);
            Assert.Equal("keep me", model.CommitMessage); Assert.False(model.IsWriting);
            Assert.Equal(before, repo.Git("rev-parse", "HEAD").Trim());
            Assert.Equal("changed\n", repo.Git("show", ":a.txt"));
            Assert.True(model.CanCommit); Assert.True(operation.Snapshot().TerminationRequested);
            Assert.False(operation.Snapshot().Running); Assert.False(operation.Terminate(pid));
        }
        finally
        {
            repo.Write("release-hook", "go");
            await write.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static TestRepository Fixture()
    {
        var repo = new TestRepository(); repo.Write("a.txt", "base\n"); repo.Commit("base");
        repo.Write("a.txt", "changed\n"); repo.Git("add", "a.txt");
        repo.Write(".git/hooks/pre-commit", "#!/bin/sh\necho 'hook ready' >&2\nwhile [ ! -f release-hook ]; do sleep 0.1; done\necho 'hook done' >&2\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(repo.Root, ".git/hooks/pre-commit"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return repo;
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
