using System.Diagnostics;
using System.Text;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class WorktreeRemovalTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Clean_linked_tree_is_removed_but_branch_and_other_data_remain(bool detached, bool fromLinked)
    {
        using var repo = Fixture(); var linked = Add(repo, "待删除 tree", detached);
        var source = fromLinked ? Add(repo, "source") : repo.Root;
        var sourceHead = Git(source, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(source, "a"), "source dirty");
        var before = Git(source, "status", "--porcelain=v1");
        var snapshot = await WorktreeCatalog.ReadAsync(source); var target = Entry(snapshot, linked);
        var plan = await WorktreeRemoval.PrepareAsync(snapshot, target);
        Assert.True(Directory.Exists(linked)); // Preparing/then cancelling never removes files.
        var result = await WorktreeRemoval.RemoveAsync(plan);
        Assert.False(Directory.Exists(linked)); Assert.Contains(plan.Target.Root, result);
        Assert.DoesNotContain((await WorktreeCatalog.ReadAsync(source)).Entries, e => WorktreeCatalog.SamePath(e.Root, linked));
        Assert.Equal(sourceHead, Git(source, "rev-parse", "HEAD")); Assert.Equal(before, Git(source, "status", "--porcelain=v1"));
        Assert.Equal("source dirty", File.ReadAllText(Path.Combine(source, "a")));
        if (!detached) Assert.NotEmpty(Git(source, "show-ref", "refs/heads/待删除 tree".Replace(" ", "-")));
    }

    [Fact]
    public async Task Current_main_locked_missing_and_foreign_selections_are_refused()
    {
        using var repo = Fixture(); var linked = Add(repo, "linked");
        var catalog = await WorktreeCatalog.ReadAsync(linked);
        await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.PrepareAsync(catalog, Entry(catalog, repo.Root)));
        await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.PrepareAsync(catalog, Entry(catalog, linked)));
        var source = await WorktreeCatalog.ReadAsync(repo.Root); var selected = Entry(source, linked);
        await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.PrepareAsync(source, selected with { Root = Path.Combine(repo.Container, "foreign") }));
        repo.Git("worktree", "lock", "--reason", "test owner", linked);
        await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.PrepareAsync(source, selected));
        repo.Git("worktree", "unlock", linked);
        Directory.Move(linked, linked + "-moved");
        await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.PrepareAsync(source, selected));
        Assert.True(Directory.Exists(linked + "-moved")); Assert.Equal(2, (await WorktreeCatalog.ReadAsync(repo.Root)).Entries.Count);
    }

    [Theory]
    [InlineData("tracked")]
    [InlineData("staged")]
    [InlineData("untracked")]
    [InlineData("ignored")]
    [InlineData("assume-unchanged")]
    [InlineData("skip-worktree")]
    public async Task Every_kind_of_local_data_blocks_removal(string kind)
    {
        using var repo = Fixture(); var linked = Add(repo, "linked");
        var name = kind == "ignored" ? "cache/valuable.bin" : kind == "untracked" ? "未跟踪.txt" : "a";
        if (kind == "assume-unchanged" || kind == "skip-worktree") Git(linked, "update-index", "--" + kind, "a");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(linked, name))!);
        File.WriteAllText(Path.Combine(linked, name), "keep me");
        if (kind == "staged") Git(linked, "add", "a");
        var source = await WorktreeCatalog.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.PrepareAsync(source, Entry(source, linked)));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(linked, name))); Assert.True(Directory.Exists(linked));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("ignored")]
    [InlineData("locked")]
    [InlineData("commit")]
    [InlineData("index-lock")]
    public async Task State_is_rechecked_after_confirmation(string change)
    {
        using var repo = Fixture(); var linked = Add(repo, "linked");
        var source = await WorktreeCatalog.ReadAsync(repo.Root); var plan = await WorktreeRemoval.PrepareAsync(source, Entry(source, linked));
        if (change is "dirty" or "commit") { File.WriteAllText(Path.Combine(linked, "a"), "changed"); if (change == "commit") { Git(linked, "add", "a"); Git(linked, "commit", "-m", "external commit"); } }
        if (change == "ignored") { Directory.CreateDirectory(Path.Combine(linked, "cache")); File.WriteAllText(Path.Combine(linked, "cache", "data"), "valuable"); }
        if (change == "locked") repo.Git("worktree", "lock", linked);
        if (change == "index-lock") File.WriteAllText(Path.Combine(plan.GitDirectory, "index.lock"), "owned by test");
        try { await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.RemoveAsync(plan)); Assert.True(Directory.Exists(linked)); }
        finally { if (change == "index-lock") File.Delete(Path.Combine(plan.GitDirectory, "index.lock")); }
    }

    [Fact]
    public async Task Cancellation_before_confirmation_and_pending_operation_preserve_tree()
    {
        using var repo = Fixture(); var linked = Add(repo, "linked"); var source = await WorktreeCatalog.ReadAsync(repo.Root);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorktreeRemoval.PrepareAsync(source, Entry(source, linked), cancelled.Token));
        var plan = await WorktreeRemoval.PrepareAsync(source, Entry(source, linked));
        var marker = Path.Combine(plan.GitDirectory, "CHERRY_PICK_HEAD"); File.WriteAllText(marker, plan.Target.Head);
        try { await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.RemoveAsync(plan)); Assert.True(Directory.Exists(linked)); }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task Clean_submodule_tree_is_not_removed()
    {
        using var repo = Fixture(); var linked = Add(repo, "linked");
        Git(linked, "-c", "protocol.file.allow=always", "submodule", "add", repo.Root, "sub");
        Git(linked, "commit", "-am", "submodule");
        Assert.Empty(Git(linked, "status", "--porcelain=v1"));
        var source = await WorktreeCatalog.ReadAsync(repo.Root);
        var error = await Assert.ThrowsAsync<IOException>(() => WorktreeRemoval.PrepareAsync(source, Entry(source, linked)));
        Assert.Contains("子模块", error.Message); Assert.True(File.Exists(Path.Combine(linked, "sub", "a")));
    }

    private static TestRepository Fixture()
    { var repo = new TestRepository(); repo.Write("a", "base"); repo.Write(".gitignore", "cache/\n"); repo.Commit("base"); return repo; }
    private static string Add(TestRepository repo, string name, bool detached = false)
    {
        var root = Path.Combine(repo.Container, name);
        if (detached) repo.Git("worktree", "add", "--detach", root); else repo.Git("worktree", "add", "-b", name.Replace(" ", "-"), root);
        return root;
    }
    private static WorktreeEntry Entry(WorktreeSnapshot snapshot, string root) => snapshot.Entries.Single(e => WorktreeCatalog.SamePath(e.Root, root));
    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(error); return stdout;
    }
}
