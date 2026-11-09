using GitX.Core;
using LibGit2Sharp;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryRewriteTests
{
    [Fact]
    public async Task Message_only_amend_keeps_original_tree_parents_author_and_staged_and_unstaged_contents()
    {
        using var repo = History(); var head = Head(repo); var tree = repo.Git("rev-parse", "HEAD^{tree}");
        var parents = repo.Git("show", "-s", "--format=%P", "HEAD"); var author = repo.Git("show", "-s", "--format=%an <%ae> %aI", "HEAD");
        repo.Write("a", "staged\n"); repo.Git("add", "a"); repo.Write("a", "unstaged\n"); repo.Write("untracked", "keep");
        var context = await RepositoryActions.ReadAsync(repo.Root); var index = Index(repo);
        await HistoryRewrite.RewordHeadAsync(context, head, "新说明\n\nbody ' $() `ticks`\n");
        Assert.Equal(tree, repo.Git("rev-parse", "HEAD^{tree}")); Assert.Equal(parents, repo.Git("show", "-s", "--format=%P", "HEAD"));
        Assert.Equal(author, repo.Git("show", "-s", "--format=%an <%ae> %aI", "HEAD"));
        Assert.Equal("新说明\n\nbody ' $() `ticks`", repo.Git("log", "-1", "--format=%B").TrimEnd());
        Assert.Equal(index, Index(repo)); Assert.Equal("unstaged\n", Read(repo, "a")); Assert.Equal("keep", Read(repo, "untracked"));
        Assert.Equal("staged", repo.Git("show", ":a").Trim()); Assert.Contains(head, repo.Git("for-each-ref", "--format=%(objectname)", "refs/gitx/recovery/"));
    }

    [Fact]
    public async Task Undo_retains_index_bytes_workfiles_and_original_history_recovery()
    {
        using var repo = History(); var head = Head(repo); var parent = repo.Git("rev-parse", "HEAD^").Trim();
        repo.Write("a", "staged\n"); repo.Git("add", "a"); repo.Write("a", "unstaged\n"); repo.Write("other", "keep");
        var context = await RepositoryActions.ReadAsync(repo.Root); var index = Index(repo);
        await HistoryRewrite.UndoAsync(context, head);
        Assert.Equal(parent, Head(repo)); Assert.Equal(index, Index(repo)); Assert.Equal("unstaged\n", Read(repo, "a")); Assert.Equal("keep", Read(repo, "other"));
        Assert.Contains(head, repo.Git("for-each-ref", "--format=%(objectname)", "refs/gitx/recovery/"));
    }

    [Theory]
    [InlineData(RewriteAction.Reword)]
    [InlineData(RewriteAction.Squash)]
    [InlineData(RewriteAction.Fixup)]
    public async Task Native_rebase_preserves_final_tree_and_other_refs_and_worktrees(RewriteAction action)
    {
        using var repo = History(); var head = Head(repo); var tree = repo.Git("rev-parse", "HEAD^{tree}");
        var oldest = repo.Git("rev-parse", "HEAD~2").Trim(); repo.Git("branch", "keep", oldest);
        var other = Path.Combine(repo.Container, "other"); repo.Git("worktree", "add", "--detach", other);
        var otherIndex = File.ReadAllBytes(repo.Git("-C", other, "rev-parse", "--git-path", "index").Trim());
        repo.Git("config", "rebase.updateRefs", "true"); repo.Git("config", "rebase.autoSquash", "true");
        repo.Git("config", "rebase.abbreviateCommands", "true");
        var plan = await HistoryRewrite.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), oldest);
        var steps = plan.Commits.Select((c, i) => new RewriteStep(c.Sha, i == 1 ? action : RewriteAction.Pick, "替换说明\n\n'quotes' $(touch injected) `literal`\n")).ToArray();
        await HistoryRewrite.ExecuteAsync(plan, steps);
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".git/rebase-merge"))); Assert.Equal(tree, repo.Git("rev-parse", "HEAD^{tree}"));
        Assert.Equal(oldest, repo.Git("rev-parse", "keep").Trim()); Assert.Equal(head, repo.Git("-C", other, "rev-parse", "HEAD").Trim());
        Assert.Equal(otherIndex, File.ReadAllBytes(repo.Git("-C", other, "rev-parse", "--git-path", "index").Trim()));
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(other, "a"))); Assert.False(File.Exists(Path.Combine(repo.Root, "injected")));
        var log = repo.Git("log", "--format=%B");
        if (action == RewriteAction.Reword) Assert.Contains("替换说明\n\n'quotes' $(touch injected) `literal`", log);
        else { Assert.Equal("3", repo.Git("rev-list", "--count", "HEAD").Trim()); Assert.Equal(action == RewriteAction.Squash, log.Contains("two")); }
    }

    [Theory]
    [InlineData("continue", true)]
    [InlineData("skip", true)]
    [InlineData("abort", true)]
    [InlineData("continue", false)]
    public async Task Conflicting_reorder_retains_native_queue_and_correct_reword_after_restart(string control, bool rewordConflict)
    {
        using var repo = History(); var before = Head(repo); var oldest = repo.Git("rev-parse", "HEAD~2").Trim();
        var plan = await HistoryRewrite.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), oldest);
        var steps = new[] { new RewriteStep(plan.Commits[1].Sha, rewordConflict ? RewriteAction.Reword : RewriteAction.Pick, "two rewritten"),
            new RewriteStep(plan.Commits[0].Sha, RewriteAction.Drop), new RewriteStep(plan.Commits[2].Sha, RewriteAction.Reword, "last rewritten") };
        var output = await HistoryRewrite.ExecuteAsync(plan, steps); Assert.Contains("暂停", output); Assert.NotEmpty(repo.Git("ls-files", "-u"));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".git/rebase-merge/grove-history-edit.json")));
        if (control == "continue") { repo.Write("a", "two\n"); repo.Git("add", "a"); }
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, control == "abort", control == "skip");
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".git/rebase-merge"))); Assert.Empty(repo.Git("ls-files", "-u"));
        if (control == "abort") { Assert.Equal(before, Head(repo)); Assert.Equal("two\n", Read(repo, "a")); }
        else
        {
            Assert.Equal("last rewritten", repo.Git("log", "-1", "--format=%B").Trim()); Assert.Equal("last\n", Read(repo, "last"));
            Assert.Equal(control == "skip" ? "base\n" : "two\n", Read(repo, "a"));
            Assert.Equal(control == "continue" && rewordConflict, repo.Git("log", "--format=%B").Contains("two rewritten"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_reword_and_drop_are_supported_without_an_artificial_root(bool drop)
    {
        using var repo = History(); var root = repo.Git("rev-list", "--max-parents=0", "HEAD").Trim();
        var plan = await HistoryRewrite.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), root);
        var steps = plan.Commits.Select((c, i) => new RewriteStep(c.Sha, i == 0 ? drop ? RewriteAction.Drop : RewriteAction.Reword : RewriteAction.Pick, "root rewritten")).ToArray();
        if (drop) { // The next commit depends on the root's a file; deliberately stop and abort.
            await HistoryRewrite.ExecuteAsync(plan, steps); using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
            Assert.Equal("rebase", snapshot.Operation?.Kind); await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, true);
        }
        else { await HistoryRewrite.ExecuteAsync(plan, steps); Assert.Equal("root rewritten", repo.Git("log", "--reverse", "--format=%s").Split('\n')[0]); Assert.Equal("4", repo.Git("rev-list", "--count", "HEAD").Trim()); }
    }

    [Fact]
    public async Task Dropping_the_full_nonroot_range_moves_branch_to_base()
    {
        using var repo = History(); var oldest = repo.Git("rev-parse", "HEAD~2").Trim(); var plan = await HistoryRewrite.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), oldest);
        await HistoryRewrite.ExecuteAsync(plan, plan.Commits.Select(c => new RewriteStep(c.Sha, RewriteAction.Drop)).ToArray());
        Assert.Equal(plan.Base, Head(repo)); Assert.Equal("base\n", Read(repo, "a")); Assert.False(File.Exists(Path.Combine(repo.Root, "last")));
    }

    [Fact]
    public async Task Invalid_or_stale_plans_and_published_detached_or_dirty_histories_are_rejected_before_changes()
    {
        using var repo = History(); var head = Head(repo); var oldest = repo.Git("rev-parse", "HEAD~2").Trim();
        var context = await RepositoryActions.ReadAsync(repo.Root); var plan = await HistoryRewrite.PrepareAsync(context, oldest);
        var steps = plan.Commits.Select(c => new RewriteStep(c.Sha, RewriteAction.Pick)).ToArray();
        Assert.Throws<IOException>(() => HistoryRewrite.Validate(plan, steps));
        Assert.Throws<IOException>(() => HistoryRewrite.Validate(plan, [steps[0], steps[0], steps[2]]));
        Assert.Throws<IOException>(() => HistoryRewrite.Validate(plan, [steps[0] with { Action = RewriteAction.Fixup }, steps[1], steps[2]]));
        repo.Write("untracked", "keep"); await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.ExecuteAsync(plan, [steps[0] with { Action = RewriteAction.Drop }, steps[1], steps[2]]));
        var dirtyContext = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.PrepareAsync(dirtyContext, oldest));
        repo.Git("add", "untracked"); repo.Git("commit", "-m", "new");
        repo.Git("update-ref", "refs/remotes/origin/shared", head);
        context = await RepositoryActions.ReadAsync(repo.Root); await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.PrepareAsync(context, oldest));
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.RewordHeadAsync(context, head, "invalid target"));
        repo.Git("checkout", "--detach", "HEAD"); context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.PrepareAsync(context, Head(repo)));
        Assert.Equal("keep", Read(repo, "untracked"));
    }

    [Fact]
    public async Task Altered_todo_invalidates_captured_confirmation_and_altered_message_refuses_continue_but_allows_abort()
    {
        using var repo = History(); var before = Head(repo); var plan = await HistoryRewrite.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), repo.Git("rev-parse", "HEAD~2").Trim());
        await HistoryRewrite.ExecuteAsync(plan, [new(plan.Commits[1].Sha, RewriteAction.Reword, "two new"), new(plan.Commits[0].Sha, RewriteAction.Drop), new(plan.Commits[2].Sha, RewriteAction.Pick)]);
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        await File.AppendAllTextAsync(Path.Combine(repo.Root, ".git/rebase-merge/git-rebase-todo"), "\n# external change\n");
        await Assert.ThrowsAsync<IOException>(() => new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, true));
        var message = Directory.GetFiles(Path.Combine(repo.Root, ".git/gitx-recovery"), "message-*.txt", SearchOption.AllDirectories).Single(); File.WriteAllText(message, "changed");
        repo.Write("a", "two\n"); repo.Git("add", "a"); snapshot = await reader.OpenAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, false));
        snapshot = await reader.OpenAsync(repo.Root); await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, true);
        Assert.Equal(before, Head(repo)); Assert.Equal("two\n", Read(repo, "a"));
    }

    [Theory]
    [InlineData(RewriteAction.Squash, false)]
    [InlineData(RewriteAction.Fixup, false)]
    [InlineData(RewriteAction.Squash, true)]
    [InlineData(RewriteAction.Fixup, true)]
    [InlineData(RewriteAction.Drop, false)]
    public async Task Selection_shortcuts_use_real_history_order_and_include_the_previous_commit_when_needed(RewriteAction action, bool multiple)
    {
        using var repo = History(); var context = await RepositoryActions.ReadAsync(repo.Root); var head = Head(repo); var tree = repo.Git("rev-parse", "HEAD^{tree}");
        var previous = repo.Git("rev-parse", "HEAD^").Trim();
        var choice = await HistoryRewrite.PrepareSelectionAsync(context, multiple ? [head, previous] : [head], action);
        Assert.Equal(action == RewriteAction.Drop ? 1 : 2, choice.Plan.Commits.Count);
        Assert.Equal(action, choice.Steps[^1].Action);
        await HistoryRewrite.ExecuteAsync(choice.Plan, choice.Steps);
        Assert.Equal("3", repo.Git("rev-list", "--count", "HEAD").Trim());
        if (action == RewriteAction.Drop) Assert.Equal(previous, Head(repo)); else Assert.Equal(tree, repo.Git("rev-parse", "HEAD^{tree}"));
    }

    [Fact]
    public async Task Noncontiguous_combine_duplicates_root_undo_and_dropping_all_roots_are_rejected()
    {
        using var repo = History(); var context = await RepositoryActions.ReadAsync(repo.Root); var head = Head(repo); var oldest = repo.Git("rev-parse", "HEAD~2").Trim();
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.PrepareSelectionAsync(context, [head, oldest], RewriteAction.Squash));
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.PrepareSelectionAsync(context, [head, head], RewriteAction.Drop));
        var root = repo.Git("rev-list", "--max-parents=0", "HEAD").Trim(); var plan = await HistoryRewrite.PrepareAsync(context, root);
        Assert.Throws<IOException>(() => HistoryRewrite.Validate(plan, plan.Commits.Select(c => new RewriteStep(c.Sha, RewriteAction.Drop)).ToArray()));
        repo.Git("checkout", "-b", "root-only", root); context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.UndoAsync(context, root)); Assert.Equal(root, Head(repo));
    }

    [Fact]
    public async Task Reword_merge_head_keeps_both_parents_but_undo_and_rebase_refuse_merge_history()
    {
        using var repo = History(); repo.Git("checkout", "-b", "topic", "HEAD^"); repo.Write("topic", "topic"); repo.Commit("topic");
        repo.Git("checkout", "main"); repo.Git("merge", "--no-ff", "--no-edit", "topic"); var head = Head(repo);
        var parents = repo.Git("show", "-s", "--format=%P", "HEAD"); var tree = repo.Git("rev-parse", "HEAD^{tree}"); var context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.UndoAsync(context, head));
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.PrepareAsync(context, head));
        await HistoryRewrite.RewordHeadAsync(context, head, "merge renamed"); Assert.Equal(parents, repo.Git("show", "-s", "--format=%P", "HEAD")); Assert.Equal(tree, repo.Git("rev-parse", "HEAD^{tree}"));
    }

    [Fact]
    public async Task Published_head_and_shared_checkout_cannot_be_rewritten_or_undone()
    {
        using var repo = History(); var head = Head(repo); var index = Index(repo);
        repo.Git("update-ref", "refs/remotes/origin/shared", head); var context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.RewordHeadAsync(context, head, "no")); await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.UndoAsync(context, head));
        repo.Git("update-ref", "-d", "refs/remotes/origin/shared"); var other = Path.Combine(repo.Container, "other"); repo.Git("worktree", "add", "--force", other, "main");
        var otherIndex = File.ReadAllBytes(repo.Git("-C", other, "rev-parse", "--git-path", "index").Trim()); context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.RewordHeadAsync(context, head, "no")); await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.UndoAsync(context, head));
        Assert.Equal(head, Head(repo)); Assert.Equal(index, Index(repo)); Assert.Equal(head, repo.Git("-C", other, "rev-parse", "HEAD").Trim());
        Assert.Equal(otherIndex, File.ReadAllBytes(repo.Git("-C", other, "rev-parse", "--git-path", "index").Trim()));
    }

    [Fact]
    public async Task Quoted_unicode_repository_path_and_multiple_reword_messages_are_literal()
    {
        using var repo = History(); var quoted = Path.Combine(repo.Container, "repo ' 中文 $literal [x]"); repo.Git("clone", "--no-local", repo.Root, quoted);
        repo.Git("-C", quoted, "remote", "remove", "origin"); // This fixture represents unpublished local history.
        repo.Git("-C", quoted, "config", "user.name", "QA"); repo.Git("-C", quoted, "config", "user.email", "qa@example.invalid");
        var context = await RepositoryActions.ReadAsync(quoted); var plan = await HistoryRewrite.PrepareAsync(context, repo.Git("rev-parse", "HEAD~2").Trim());
        var messages = new[] { "one $(literal)\n\nbody", "two 'quoted' `ticks`", "last\n\n# literal body" };
        await HistoryRewrite.ExecuteAsync(plan, plan.Commits.Select((c, i) => new RewriteStep(c.Sha, RewriteAction.Reword, messages[i])).ToArray());
        for (var i = 0; i < 3; i++) Assert.Equal(messages[i], repo.Git("-C", quoted, "show", "-s", "--format=%B", "HEAD~" + (2 - i)).TrimEnd());
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(quoted, "a"))); Assert.Equal("last\n", File.ReadAllText(Path.Combine(quoted, "last")));
    }

    [Fact]
    public async Task Range_is_bounded_to_one_hundred_actual_commits()
    {
        using var repo = History(); var old = Head(repo);
        using (var native = new Repository(repo.Root))
        {
            var signature = native.Config.BuildSignature(DateTimeOffset.Now);
            for (var i = 0; i < 100; i++) native.Commit("empty " + i, signature, signature, new CommitOptions { AllowEmptyCommit = true });
        }
        var context = await RepositoryActions.ReadAsync(repo.Root);
        await Assert.ThrowsAsync<IOException>(() => HistoryRewrite.PrepareAsync(context, old));
        var plan = await HistoryRewrite.PrepareAsync(context, repo.Git("rev-parse", "HEAD~99").Trim()); Assert.Equal(100, plan.Commits.Count);
    }

    [Fact]
    public async Task Maximum_sized_reword_message_is_not_rejected_for_its_canonical_final_newline()
    {
        using var repo = History(); var plan = await HistoryRewrite.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), Head(repo));
        var message = new string('x', 64 * 1024);
        await HistoryRewrite.ExecuteAsync(plan, [new(Head(repo), RewriteAction.Reword, message)]);
        Assert.Equal(message, repo.Git("log", "-1", "--format=%B").TrimEnd()); Assert.Equal("two\n", Read(repo, "a"));
    }

    private static TestRepository History()
    {
        var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base"); repo.Write("a", "one\n"); repo.Commit("one");
        repo.Write("a", "two\n"); repo.Commit("two"); repo.Write("last", "last\n"); repo.Commit("last"); return repo;
    }
    private static byte[] Index(TestRepository repo) => File.ReadAllBytes(Path.Combine(repo.Root, ".git/index"));
    private static string Head(TestRepository repo) => repo.Git("rev-parse", "HEAD").Trim();
    private static string Read(TestRepository repo, string path) => File.ReadAllText(Path.Combine(repo.Root, path));
}
