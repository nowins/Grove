using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryWriterTests
{
    [Theory]
    [InlineData(HistoryAction.ResetSoft)]
    [InlineData(HistoryAction.ResetMixed)]
    [InlineData(HistoryAction.ResetHard)]
    public async Task Reset_has_exact_HEAD_index_worktree_semantics_and_recovery(HistoryAction action)
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base"); var target = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "committed"); repo.Commit("next"); var old = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "staged"); repo.Git("add", "a"); repo.Write("a", "working");
        var context = await RepositoryActions.ReadAsync(repo.Root);
        var result = await new HistoryWriter().ExecuteAsync(context, action, target);
        Assert.Equal(target, repo.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(action == HistoryAction.ResetSoft ? "staged" : "base", repo.Git("show", ":a"));
        Assert.Equal(action == HistoryAction.ResetHard ? "base" : "working", File.ReadAllText(Path.Combine(repo.Root, "a")));
        var recovery = Directory.GetDirectories(Path.Combine(repo.Root, ".git", "gitx-recovery")).Single();
        Assert.Contains(recovery, result); Assert.Equal("working", File.ReadAllText(Path.Combine(recovery, "0.content")));
        Assert.Equal("staged", File.ReadAllText(Path.Combine(recovery, "0.content.index")));
        Assert.Equal(old, repo.Git("rev-parse", "refs/gitx/recovery/" + Path.GetFileName(recovery)).Trim());
    }

    [Fact]
    public async Task Hard_reset_backs_up_obstructing_ignored_file_and_refuses_directory()
    {
        using var repo = new TestRepository(); repo.Write("a", "tracked"); repo.Commit("target"); var target = repo.Git("rev-parse", "HEAD").Trim();
        repo.Git("rm", "a"); repo.Write(".gitignore", "a\n"); repo.Commit("remove and ignore"); repo.Write("a", "ignored content");
        await new HistoryWriter().ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), HistoryAction.ResetHard, target);
        var recovery = Directory.GetDirectories(Path.Combine(repo.Root, ".git", "gitx-recovery")).Single();
        Assert.Contains(Directory.GetFiles(recovery, "*.content"), f => File.ReadAllText(f) == "ignored content");
        repo.Git("rm", "a"); repo.Commit("remove"); repo.Write("a/nested", "must survive");
        await Assert.ThrowsAsync<IOException>(() => new HistoryWriter().ExecuteAsync(RepositoryActions.ReadAsync(repo.Root).Result, HistoryAction.ResetHard, target));
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(repo.Root, "a/nested")));
    }

    [Fact]
    public async Task Cherry_pick_and_revert_create_correct_trees()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Write("b", "topic"); repo.Commit("topic"); var target = repo.Git("rev-parse", "HEAD").Trim(); repo.Git("checkout", "main");
        var writer = new HistoryWriter(); await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), HistoryAction.CherryPick, target);
        Assert.Equal("topic", repo.Git("show", "HEAD:b"));
        await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), HistoryAction.Revert, repo.Git("rev-parse", "HEAD").Trim());
        Assert.False(File.Exists(Path.Combine(repo.Root, "b"))); Assert.Equal("3", repo.Git("rev-list", "--count", "HEAD").Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revert_conflicts_support_continue_and_abort(bool abort)
    {
        using var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base"); repo.Write("a", "second\n"); repo.Commit("second"); var second = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "third\n"); repo.Commit("third");
        await new HistoryWriter().ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), HistoryAction.Revert, second);
        var snapshot = await new RepositoryReader().OpenAsync(repo.Root); Assert.Equal("revert", snapshot.Operation!.Kind);
        var writer = new ConflictWriter();
        if (!abort)
        {
            var doc = await new ConflictReader().ReadAsync(repo.Root, "a"); var token = await writer.SaveAsync(doc, doc.Token, "manual resolution\n"); await writer.ResolveAsync(doc, token);
            snapshot = await new RepositoryReader().OpenAsync(repo.Root);
        }
        await writer.OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, abort);
        Assert.Null((await new RepositoryReader().OpenAsync(repo.Root)).Operation); Assert.Empty(repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Amend_only_uses_index_and_hook_failure_preserves_original_commit()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base"); var old = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "staged"); repo.Git("add", "a"); repo.Write("a", "working");
        repo.Write(".git/hooks/pre-commit", "#!/bin/sh\necho hook-rejected >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(repo.Root, ".git/hooks/pre-commit"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var writer = new HistoryWriter(); var context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => writer.AmendAsync(context, "中文说明")); Assert.Equal(old, repo.Git("rev-parse", "HEAD").Trim());
        File.Delete(Path.Combine(repo.Root, ".git/hooks/pre-commit"));
        await writer.AmendAsync(await RepositoryActions.ReadAsync(repo.Root), "中文说明\n\n正文");
        Assert.Equal("staged", repo.Git("show", "HEAD:a")); Assert.Equal("working", File.ReadAllText(Path.Combine(repo.Root, "a")));
        Assert.Equal("1", repo.Git("rev-list", "--count", "HEAD").Trim()); Assert.Contains("中文说明", repo.Git("log", "-1", "--format=%B"));
    }

    [Fact]
    public async Task Stash_roundtrip_restores_index_worktree_untracked_and_retains_apply()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base"); repo.Write("a", "staged"); repo.Git("add", "a"); repo.Write("a", "working"); repo.Write("new", "untracked");
        var writer = new StashWriter(); await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), StashAction.Save, message: "测试保存");
        Assert.Empty(repo.Git("status", "--porcelain")); var context = await RepositoryActions.ReadAsync(repo.Root);
        await writer.ExecuteAsync(context, StashAction.Apply, context.Stashes.Single());
        Assert.Equal("staged", repo.Git("show", ":a")); Assert.Equal("working", File.ReadAllText(Path.Combine(repo.Root, "a"))); Assert.True(File.Exists(Path.Combine(repo.Root, "new")));
        Assert.Single((await RepositoryActions.ReadAsync(repo.Root)).Stashes);
        context = await RepositoryActions.ReadAsync(repo.Root); await writer.ExecuteAsync(context, StashAction.Drop, context.Stashes.Single());
        Assert.Empty(repo.Git("stash", "list"));
    }

    [Fact]
    public async Task Stash_pop_conflict_keeps_stash()
    {
        using var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base"); repo.Write("a", "stashed\n"); var writer = new StashWriter();
        await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), StashAction.Save);
        repo.Write("a", "new head\n"); repo.Commit("new head"); var context = await RepositoryActions.ReadAsync(repo.Root);
        await writer.ExecuteAsync(context, StashAction.Pop, context.Stashes.Single());
        Assert.NotEmpty(repo.Git("ls-files", "-u")); Assert.Single((await RepositoryActions.ReadAsync(repo.Root)).Stashes);
    }

    [Fact]
    public async Task Stale_stash_order_cannot_drop_another_saved_change()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var writer = new StashWriter(); repo.Write("a", "one");
        await writer.ExecuteAsync(await RepositoryActions.ReadAsync(repo.Root), StashAction.Save, message: "one");
        var context = await RepositoryActions.ReadAsync(repo.Root);
        repo.Write("a", "two"); repo.Git("stash", "push", "-m", "two");
        await Assert.ThrowsAsync<IOException>(() => writer.ExecuteAsync(context, StashAction.Drop, context.Stashes.Single()));
        Assert.Equal(2, (await RepositoryActions.ReadAsync(repo.Root)).Stashes.Count);
    }
}
