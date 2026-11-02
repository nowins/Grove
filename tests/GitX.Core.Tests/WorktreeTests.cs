using System.Diagnostics;
using System.Text;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class WorktreeTests
{
    [Fact]
    public async Task Discovery_from_linked_tree_preserves_paths_detached_and_lock_reason_without_writes()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var linked = Path.Combine(repo.Container, "关联 工作树"); var detached = Path.Combine(repo.Container, "detached tree");
        repo.Git("worktree", "add", "-b", "topic", linked);
        repo.Git("worktree", "add", "--detach", detached);
        repo.Git("worktree", "lock", "--reason", "保留\n便携磁盘", linked);
        repo.Write("a", "main dirty"); File.WriteAllText(Path.Combine(linked, "a"), "linked dirty");
        var before = repo.Git("status", "--porcelain=v1"); var linkedBefore = Git(linked, "status", "--porcelain=v1");
        var catalog = await WorktreeCatalog.ReadAsync(linked);
        Assert.Equal(3, catalog.Entries.Count);
        var current = Assert.Single(catalog.Entries, e => e.Current);
        Assert.True(WorktreeCatalog.SamePath(linked, current.Root)); Assert.Equal("topic", current.BranchLabel);
        Assert.Equal("保留\n便携磁盘", current.LockedReason); Assert.Null(current.UnavailableReason);
        var detachedEntry = Assert.Single(catalog.Entries, e => e.Detached);
        Assert.Equal($"分离 HEAD · {detachedEntry.Head![..8]}", detachedEntry.BranchLabel);
        Assert.Contains(current.Root, current.AccessibleName); Assert.Contains("已锁定", current.AccessibleName);
        Assert.True((await RepositoryActions.ReadAsync(detached)).Detached);
        var main = Assert.Single(catalog.Entries, e => e.BranchLabel == "main");
        Assert.True(WorktreeCatalog.SamePath(repo.Root, await WorktreeCatalog.ResolveOpenAsync(catalog, main)));
        // Resolving or cancelling a selector does not checkout, reset, or carry edits across trees.
        Assert.Equal(before, repo.Git("status", "--porcelain=v1")); Assert.Equal(linkedBefore, Git(linked, "status", "--porcelain=v1"));
        Assert.Equal("main dirty", File.ReadAllText(Path.Combine(repo.Root, "a")));
        Assert.Equal("linked dirty", File.ReadAllText(Path.Combine(linked, "a")));
    }

    [Fact]
    public async Task Missing_and_replaced_worktree_paths_are_rejected_without_creating_or_pruning()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var linked = Path.Combine(repo.Container, "linked"); repo.Git("worktree", "add", "-b", "topic", linked);
        var catalog = await WorktreeCatalog.ReadAsync(repo.Root); var selected = Assert.Single(catalog.Entries, e => !e.Current);
        var moved = Path.Combine(repo.Container, "moved"); Directory.Move(linked, moved);
        var missing = await WorktreeCatalog.ReadAsync(repo.Root);
        Assert.NotNull(Assert.Single(missing.Entries, e => !e.Current).UnavailableReason);
        await Assert.ThrowsAsync<IOException>(() => WorktreeCatalog.ResolveOpenAsync(catalog, selected));
        Assert.False(Directory.Exists(linked)); Assert.Equal(2, (await WorktreeCatalog.ReadAsync(repo.Root)).Entries.Count);
        Directory.CreateDirectory(linked); Git(linked, "init", "-b", "foreign");
        await Assert.ThrowsAsync<IOException>(() => WorktreeCatalog.ResolveOpenAsync(catalog, selected));
        Assert.Equal("foreign", Git(linked, "symbolic-ref", "--short", "HEAD").Trim());
        Assert.Equal("base", File.ReadAllText(Path.Combine(moved, "a")));
    }

    [Fact]
    public async Task Bare_source_is_listed_but_cannot_be_opened_as_a_worktree()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        var bare = Path.Combine(repo.Container, "bare.git"); repo.Git("clone", "--bare", repo.Root, bare);
        var linked = Path.Combine(repo.Container, "linked"); Git(bare, "worktree", "add", linked, "main");
        var catalog = await WorktreeCatalog.ReadAsync(linked);
        var source = Assert.Single(catalog.Entries, e => e.Bare); Assert.NotNull(source.UnavailableReason);
        await Assert.ThrowsAsync<IOException>(() => WorktreeCatalog.ResolveOpenAsync(catalog, source));
    }

    [Theory]
    [InlineData(HistoryAction.ResetSoft, false)]
    [InlineData(HistoryAction.ResetMixed, false)]
    [InlineData(HistoryAction.ResetHard, false)]
    [InlineData(HistoryAction.ResetKeep, false)]
    [InlineData(HistoryAction.ResetSoft, true)]
    [InlineData(HistoryAction.ResetMixed, true)]
    [InlineData(HistoryAction.ResetHard, true)]
    [InlineData(HistoryAction.ResetKeep, true)]
    public async Task Reset_affects_only_selected_worktree_with_exact_mode_semantics(HistoryAction mode, bool detached)
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Write("local", "base"); repo.Commit("base");
        var target = repo.Git("rev-parse", "HEAD").Trim(); var linked = Path.Combine(repo.Container, "linked");
        if (detached) repo.Git("worktree", "add", "--detach", linked); else repo.Git("worktree", "add", "-b", "topic", linked);
        File.WriteAllText(Path.Combine(linked, "a"), "committed"); Git(linked, "add", "a"); Git(linked, "commit", "-m", "next");
        File.WriteAllText(Path.Combine(linked, "local"), "staged"); Git(linked, "add", "local");
        File.WriteAllText(Path.Combine(linked, "local"), "working");
        repo.Write("local", "main staged"); repo.Git("add", "local"); repo.Write("local", "main working");
        var beforeHead = repo.Git("rev-parse", "HEAD"); var beforeIndex = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index"));
        var beforeStatus = repo.Git("status", "--porcelain=v1");
        var context = await RepositoryActions.ReadAsync(linked);
        var output = await new HistoryWriter().ExecuteAsync(context, mode, target);
        Assert.Equal(target, Git(linked, "rev-parse", "HEAD").Trim());
        Assert.Equal(mode == HistoryAction.ResetSoft ? "committed" : "base", Git(linked, "show", ":a"));
        Assert.Equal(mode is HistoryAction.ResetHard or HistoryAction.ResetKeep ? "base" : "committed", File.ReadAllText(Path.Combine(linked, "a")));
        Assert.Equal(mode == HistoryAction.ResetSoft ? "staged" : "base", Git(linked, "show", ":local"));
        Assert.Equal(mode == HistoryAction.ResetHard ? "base" : "working", File.ReadAllText(Path.Combine(linked, "local")));
        Assert.Equal(detached, (await WorktreeCatalog.ReadAsync(linked)).Entries.Single(e => e.Current).Detached);
        Assert.Equal(beforeHead, repo.Git("rev-parse", "HEAD")); Assert.Equal(beforeIndex, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")));
        Assert.Equal(beforeStatus, repo.Git("status", "--porcelain=v1")); Assert.Equal("main working", File.ReadAllText(Path.Combine(repo.Root, "local")));
        Assert.Contains("恢复记录", output);
    }

    [Theory]
    [InlineData(HistoryAction.ResetSoft)]
    [InlineData(HistoryAction.ResetMixed)]
    [InlineData(HistoryAction.ResetHard)]
    [InlineData(HistoryAction.ResetKeep)]
    public async Task Reset_refuses_branch_forced_into_another_worktree(HistoryAction mode)
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base"); var target = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "next"); repo.Commit("next"); var linked = Path.Combine(repo.Container, "forced");
        repo.Git("worktree", "add", "--force", linked, "main"); var head = repo.Git("rev-parse", "HEAD");
        var error = await Assert.ThrowsAsync<IOException>(() => new HistoryWriter().ExecuteAsync(RepositoryActions.ReadAsync(repo.Root).Result, mode, target));
        Assert.Contains("其他 worktree", error.Message); Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.Equal("next", File.ReadAllText(Path.Combine(linked, "a"))); Assert.Equal("next", File.ReadAllText(Path.Combine(repo.Root, "a")));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".git", "gitx-recovery")));
    }

    [Fact]
    public async Task Keep_rejects_overlapping_local_changes_and_stale_or_invalid_targets_without_resetting()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base"); var target = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a", "next"); repo.Commit("next"); repo.Write("a", "staged"); repo.Git("add", "a"); repo.Write("a", "working");
        var context = await RepositoryActions.ReadAsync(repo.Root); var head = repo.Git("rev-parse", "HEAD"); var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index"));
        await Assert.ThrowsAsync<IOException>(() => new HistoryWriter().ExecuteAsync(context, HistoryAction.ResetKeep, target));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")));
        Assert.Equal("working", File.ReadAllText(Path.Combine(repo.Root, "a")));
        await Assert.ThrowsAsync<IOException>(() => new HistoryWriter().ExecuteAsync(context, HistoryAction.ResetKeep, new string('f', 40)));
        repo.Write("a", "external change");
        await Assert.ThrowsAsync<IOException>(() => new HistoryWriter().ExecuteAsync(context, HistoryAction.ResetKeep, target));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal("external change", File.ReadAllText(Path.Combine(repo.Root, "a")));
    }

    private static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var errors = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(errors); return output;
    }
}
