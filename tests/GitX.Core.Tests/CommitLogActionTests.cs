using System.Text;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class CommitLogActionTests
{
    [Fact]
    public async Task Branch_and_tag_use_selected_revision_and_preserve_dirty_index_worktree_and_other_worktree()
    {
        using var repo = new TestRepository();
        repo.Write("a", "first"); repo.Commit("first"); var first = Head(repo);
        repo.Write("a", "second"); repo.Commit("second");
        var linked = Path.Combine(repo.Container, "linked"); repo.Git("worktree", "add", "-b", "linked", linked);
        repo.Write("a", "staged"); repo.Git("add", "a"); repo.Write("a", "working"); repo.Write("untracked", "keep");
        repo.Git("config", "tag.gpgSign", "true");
        var before = Capture(repo); var otherBefore = File.ReadAllBytes(Path.Combine(linked, "a"));
        var writer = new CommitReferenceWriter();
        await writer.CreateBranchAsync(await RepositoryActions.ReadAsync(repo.Root), first, "review/中文");
        await writer.CreateTagAsync(await RepositoryActions.ReadAsync(repo.Root), first, "review/中文");
        Assert.Equal(first, repo.Git("rev-parse", "refs/heads/review/中文").Trim());
        Assert.Equal(first, repo.Git("rev-parse", "refs/tags/review/中文").Trim());
        Assert.Equal("commit", repo.Git("cat-file", "-t", "refs/tags/review/中文").Trim());
        Assert.Equal("", repo.Git("for-each-ref", "--format=%(upstream)", "refs/heads/review/中文").Trim());
        AssertState(before, repo); Assert.Equal(otherBefore, File.ReadAllBytes(Path.Combine(linked, "a")));
        Assert.Equal(Head(repo), repo.Git("-C", linked, "rev-parse", "HEAD").Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkout_uses_selected_commit_and_never_moves_existing_branches(bool newBranch)
    {
        using var repo = TwoCommits(out var first);
        var main = Head(repo); var writer = new CommitReferenceWriter();
        if (newBranch) await writer.CreateBranchAsync(await RepositoryActions.ReadAsync(repo.Root), first, "review", checkout: true);
        else await writer.CheckoutAsync(await RepositoryActions.ReadAsync(repo.Root), first);
        Assert.Equal(first, Head(repo)); Assert.Equal("first", File.ReadAllText(Path.Combine(repo.Root, "a")));
        Assert.Equal(main, repo.Git("rev-parse", "refs/heads/main").Trim());
        Assert.Equal(newBranch ? "review" : "", repo.Git("branch", "--show-current").Trim());
        Assert.Empty(repo.Git("--no-optional-locks", "status", "--porcelain"));
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("tag")]
    [InlineData("checkout")]
    public async Task Stale_context_cannot_write_after_disk_or_refs_change(string action)
    {
        using var repo = TwoCommits(out var first); var writer = new CommitReferenceWriter();
        var context = await RepositoryActions.ReadAsync(repo.Root);
        repo.Write("a", "external edit"); var before = Capture(repo);
        Task Run() => action switch { "branch" => writer.CreateBranchAsync(context, first, "review"), "tag" => writer.CreateTagAsync(context, first, "review"), _ => writer.CheckoutAsync(context, first) };
        await Assert.ThrowsAsync<IOException>(Run); AssertState(before, repo);
        Assert.Empty(repo.Git("for-each-ref", "--format=%(refname)", "refs/heads/review", "refs/tags/review"));
        context = await RepositoryActions.ReadAsync(repo.Root); repo.Git("branch", "external"); before = Capture(repo);
        await Assert.ThrowsAsync<IOException>(Run); AssertState(before, repo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dirty_and_ignored_obstructions_refuse_checkout_without_losing_files_or_creating_branch(bool newBranch)
    {
        using var repo = TwoCommits(out var first); var writer = new CommitReferenceWriter();
        repo.Write("untracked", "keep"); var before = Capture(repo);
        Task Run() => newBranch ? writer.CreateBranchAsync(RepositoryActions.ReadAsync(repo.Root).Result, first, "review", true)
            : writer.CheckoutAsync(RepositoryActions.ReadAsync(repo.Root).Result, first);
        await Assert.ThrowsAsync<IOException>(Run); AssertState(before, repo);
        File.Delete(Path.Combine(repo.Root, "untracked"));
        repo.Git("rm", "a"); repo.Write(".gitignore", "a\n"); repo.Commit("ignore removed path"); repo.Write("a", "ignored keep");
        before = Capture(repo); await Assert.ThrowsAsync<IOException>(Run); AssertState(before, repo);
        Assert.Empty(repo.Git("for-each-ref", "--format=%(refname)", "refs/heads/review"));
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("../outside")]
    [InlineData("--force")]
    [InlineData("@{-1}")]
    public async Task Invalid_reference_names_cannot_escape_or_mutate_repository(string name)
    {
        using var repo = TwoCommits(out var first); var writer = new CommitReferenceWriter(); var context = await RepositoryActions.ReadAsync(repo.Root);
        var before = Capture(repo); var refs = repo.Git("show-ref");
        await Assert.ThrowsAsync<IOException>(() => writer.CreateBranchAsync(context, first, name));
        await Assert.ThrowsAsync<IOException>(() => writer.CreateTagAsync(context, first, name));
        AssertState(before, repo); Assert.Equal(refs, repo.Git("show-ref"));
    }

    [Fact]
    public async Task In_progress_merge_and_stale_index_are_rejected_for_all_reference_writes()
    {
        using var repo = TwoCommits(out var first); var writer = new CommitReferenceWriter();
        var context = await RepositoryActions.ReadAsync(repo.Root);
        repo.Write("b", "stage"); repo.Git("add", "b"); var before = Capture(repo);
        await RejectAll(); AssertState(before, repo);
        repo.Git("reset", "--hard", "HEAD");
        repo.Git("checkout", "-b", "topic", first); repo.Write("a", "topic"); repo.Commit("topic"); repo.Git("checkout", "main");
        Assert.Throws<IOException>(() => repo.Git("merge", "topic")); context = await RepositoryActions.ReadAsync(repo.Root);
        Assert.True(context.InOperation); before = Capture(repo);
        await RejectAll(); AssertState(before, repo); Assert.NotEmpty(repo.Git("ls-files", "-u"));
        async Task RejectAll()
        {
            await Assert.ThrowsAsync<IOException>(() => writer.CreateBranchAsync(context, first, "review"));
            await Assert.ThrowsAsync<IOException>(() => writer.CreateTagAsync(context, first, "review"));
            await Assert.ThrowsAsync<IOException>(() => writer.CheckoutAsync(context, first));
        }
    }

    [Fact]
    public async Task Existing_references_and_noncommit_targets_are_not_replaced()
    {
        using var repo = TwoCommits(out var first); var writer = new CommitReferenceWriter();
        repo.Git("branch", "review"); repo.Git("tag", "review"); var refs = repo.Git("show-ref"); var before = Capture(repo);
        var context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => writer.CreateBranchAsync(context, first, "review"));
        await Assert.ThrowsAsync<IOException>(() => writer.CreateTagAsync(context, first, "review"));
        await Assert.ThrowsAsync<IOException>(() => writer.CreateTagAsync(context, first, "REVIEW"));
        var blob = repo.Git("rev-parse", "HEAD:a").Trim();
        await Assert.ThrowsAsync<IOException>(() => writer.CreateBranchAsync(context, blob, "new"));
        await Assert.ThrowsAsync<IOException>(() => writer.CheckoutAsync(context, "HEAD~1"));
        AssertState(before, repo); Assert.Equal(refs, repo.Git("show-ref"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Patch_round_trips_root_and_normal_commits_including_binary_non_utf8_and_renames(bool rootCommit)
    {
        using var repo = new TestRepository();
        repo.Write("中文 [x].txt", "before\r\n"); File.WriteAllBytes(Path.Combine(repo.Root, "binary"), [0, 1, 128, 255]);
        repo.Commit("initial\n\nFull message body 中文"); var first = Head(repo);
        if (!rootCommit)
        {
            repo.Git("mv", "中文 [x].txt", "renamed.txt");
            File.WriteAllBytes(Path.Combine(repo.Root, "renamed.txt"), [0x80, 0x81, 0x0d, 0x0a, 0xff]);
            File.WriteAllBytes(Path.Combine(repo.Root, "binary"), [0, 3, 129, 255]); repo.Commit("next");
        }
        var revision = Head(repo);
        repo.Git("config", "diff.noprefix", "true"); repo.Git("config", "diff.external", "must-not-run");
        var before = Capture(repo); var refs = repo.Git("show-ref");
        var bytes = await CommitLogActions.PatchAsync(repo.Root, revision);
        AssertState(before, repo); Assert.Equal(refs, repo.Git("show-ref"));
        var patch = Path.Combine(repo.Container, "export.patch"); await File.WriteAllBytesAsync(patch, bytes);
        // Applying the exported bytes in an independent repository must reproduce the exact target tree.
        using var apply = new TestRepository();
        if (!rootCommit)
        {
            apply.Git("fetch", repo.Root, first); apply.Git("checkout", "--detach", "FETCH_HEAD");
        }
        apply.Git("apply", "--index", patch);
        Assert.Equal(repo.Git("rev-parse", revision + "^{tree}").Trim(), apply.Git("write-tree").Trim());
        Assert.Equal("initial\n\nFull message body 中文\n", (await CommitLogActions.ReadAsync(repo.Root, first)).Message);
    }

    [Fact]
    public async Task Merge_patch_requires_real_parent_and_children_follow_graph_edges()
    {
        using var repo = TwoCommits(out var first); var second = Head(repo);
        repo.Git("checkout", "-b", "topic", first); repo.Write("topic", "topic"); repo.Commit("topic"); var topic = Head(repo);
        repo.Git("checkout", "main"); repo.Git("merge", "--no-ff", "--no-edit", "topic"); var merge = Head(repo);
        var before = Capture(repo);
        await Assert.ThrowsAsync<IOException>(() => CommitLogActions.PatchAsync(repo.Root, merge));
        await Assert.ThrowsAsync<IOException>(() => CommitLogActions.PatchAsync(repo.Root, merge, first));
        foreach (var parent in new[] { second, topic })
        {
            var patch = Path.Combine(repo.Container, parent + ".patch");
            await File.WriteAllBytesAsync(patch, await CommitLogActions.PatchAsync(repo.Root, merge, parent));
            using var apply = new TestRepository(); apply.Git("fetch", repo.Root, parent); apply.Git("checkout", "--detach", "FETCH_HEAD");
            apply.Git("apply", "--index", patch);
            Assert.Equal(repo.Git("rev-parse", merge + "^{tree}").Trim(), apply.Git("write-tree").Trim());
        }
        Assert.Equal(new[] { second, topic }.Order(), (await CommitLogActions.ChildrenAsync(repo.Root, first)).Select(c => c.Sha).Order());
        Assert.Equal(merge, Assert.Single(await CommitLogActions.ChildrenAsync(repo.Root, topic)).Sha);
        Assert.Empty(await CommitLogActions.ChildrenAsync(repo.Root, merge));
        AssertState(before, repo);
    }

    private static TestRepository TwoCommits(out string first)
    {
        var repo = new TestRepository(); repo.Write("a", "first"); repo.Commit("first"); first = Head(repo);
        repo.Write("a", "second"); repo.Commit("second"); return repo;
    }
    private static string Head(TestRepository repo) => repo.Git("rev-parse", "HEAD").Trim();
    private sealed record State(string Head, byte[] Index, string Status, Dictionary<string, byte[]> Files);
    private static State Capture(TestRepository repo)
    {
        var status = repo.Git("--no-optional-locks", "status", "--porcelain=v1", "-z");
        return new(Head(repo), File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")), status,
            Directory.EnumerateFiles(repo.Root, "*", SearchOption.AllDirectories).Where(p => !Path.GetRelativePath(repo.Root, p).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToDictionary(p => Path.GetRelativePath(repo.Root, p), File.ReadAllBytes));
    }
    private static void AssertState(State expected, TestRepository repo)
    {
        var actual = Capture(repo); Assert.Equal(expected.Head, actual.Head); Assert.Equal(expected.Index, actual.Index); Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Files.Keys.Order(), actual.Files.Keys.Order());
        foreach (var (path, bytes) in expected.Files) Assert.Equal(bytes, actual.Files[path]);
    }
}
