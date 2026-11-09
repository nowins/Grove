using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryBatchTests
{
    [Theory]
    [InlineData(HistoryAction.CherryPick)]
    [InlineData(HistoryAction.Revert)]
    public async Task Selected_commits_execute_in_graph_order_and_preserve_other_worktree(HistoryAction action)
    {
        using var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base"); var baseline = Head(repo);
        repo.Git("checkout", "-b", "topic"); repo.Write("a", "one\n"); repo.Commit("one"); var one = Head(repo);
        repo.Write("a", "two\n"); repo.Commit("two"); var two = Head(repo); var topicTree = repo.Git("rev-parse", "HEAD^{tree}").Trim();
        if (action == HistoryAction.CherryPick) repo.Git("checkout", "main");
        var before = Head(repo); var other = Path.Combine(repo.Container, "other"); repo.Git("worktree", "add", "--detach", other);
        var otherIndex = repo.Git("-C", other, "rev-parse", "--git-path", "index").Trim(); var indexBytes = File.ReadAllBytes(otherIndex);
        var plan = await HistoryBatch.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), action, [two, one]);
        Assert.Equal(action == HistoryAction.CherryPick ? [one, two] : new[] { two, one }, plan.Commits.Select(c => c.Sha));
        await HistoryBatch.ExecuteAsync(plan);
        Assert.Equal(action == HistoryAction.CherryPick ? topicTree : repo.Git("rev-parse", baseline + "^{tree}").Trim(), repo.Git("write-tree").Trim());
        Assert.Equal(before, repo.Git("rev-parse", "HEAD~2").Trim());
        Assert.Empty(repo.Git("--no-optional-locks", "status", "--porcelain"));
        Assert.Equal(before, repo.Git("-C", other, "rev-parse", "HEAD").Trim()); Assert.Equal(indexBytes, File.ReadAllBytes(otherIndex));
        Assert.Equal(action == HistoryAction.CherryPick ? "base\n" : "two\n", File.ReadAllText(Path.Combine(other, "a")));
    }

    [Theory]
    [InlineData(HistoryAction.CherryPick, "continue")]
    [InlineData(HistoryAction.CherryPick, "skip")]
    [InlineData(HistoryAction.CherryPick, "abort")]
    [InlineData(HistoryAction.Revert, "continue")]
    [InlineData(HistoryAction.Revert, "skip")]
    [InlineData(HistoryAction.Revert, "abort")]
    public async Task Native_sequencer_retains_remaining_commits_and_supports_continue_skip_abort(HistoryAction action, string control)
    {
        using var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Write("first", "one\n"); repo.Commit("one"); var one = Head(repo);
        repo.Write("a", "topic\n"); repo.Commit("two"); var two = Head(repo);
        repo.Write("last", "three\n"); repo.Commit("three"); var three = Head(repo);
        if (action == HistoryAction.CherryPick) repo.Git("checkout", "main");
        repo.Write("a", "main\n"); repo.Commit("local independent change"); var before = Head(repo);
        var plan = await HistoryBatch.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), action, [two, one, three]);
        await HistoryBatch.ExecuteAsync(plan);
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        Assert.Equal(action == HistoryAction.CherryPick ? "cherry-pick" : "revert", snapshot.Operation?.Kind);
        Assert.NotEmpty(repo.Git("ls-files", "-u"));
        var pending = File.ReadAllText(Path.Combine(repo.Root, ".git", "sequencer", "todo"));
        Assert.Contains((action == HistoryAction.CherryPick ? three : one)[..7], pending);
        var writer = new ConflictWriter();
        if (control == "continue")
        {
            var conflict = await new ConflictReader().ReadAsync(repo.Root, "a");
            var token = await writer.SaveAsync(conflict, conflict.Token, "resolved\n"); await writer.ResolveAsync(conflict, token);
            snapshot = await reader.OpenAsync(repo.Root);
        }
        if (control == "skip") repo.Write("a", "resolution draft to retain\n");
        if (control == "skip") snapshot = await reader.OpenAsync(repo.Root);
        await writer.OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, control == "abort", control == "skip");
        var final = await reader.OpenAsync(repo.Root); Assert.Null(final.Operation); Assert.Empty(repo.Git("--no-optional-locks", "status", "--porcelain"));
        if (control == "abort")
        {
            Assert.Equal(before, Head(repo)); Assert.Equal("main\n", File.ReadAllText(Path.Combine(repo.Root, "a")));
            Assert.Equal(action == HistoryAction.Revert, File.Exists(Path.Combine(repo.Root, "first")));
            Assert.Equal(action == HistoryAction.Revert, File.Exists(Path.Combine(repo.Root, "last")));
        }
        else
        {
            Assert.Equal(action == HistoryAction.CherryPick, File.Exists(Path.Combine(repo.Root, "first")));
            Assert.Equal(action == HistoryAction.CherryPick, File.Exists(Path.Combine(repo.Root, "last")));
            Assert.Equal(control == "continue" ? "resolved\n" : "main\n", File.ReadAllText(Path.Combine(repo.Root, "a")));
        }
        if (control == "skip") Assert.Contains(Directory.EnumerateFiles(Path.Combine(repo.Root, ".git", "gitx-recovery"), "*", SearchOption.AllDirectories),
            path => File.ReadAllBytes(path).AsSpan().IndexOf("resolution draft to retain"u8) >= 0);
    }

    [Fact]
    public async Task Stale_context_invalid_selection_and_changed_order_are_rejected_before_any_write()
    {
        using var repo = new TestRepository(); repo.Write("a", "one"); repo.Commit("one"); var one = Head(repo);
        repo.Write("b", "two"); repo.Commit("two"); var two = Head(repo); var context = await RepositoryActions.ReadAsync(repo.Root);
        var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")); var refs = repo.Git("show-ref");
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.PrepareAsync(context, HistoryAction.Revert, [one, one]));
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.PrepareAsync(context, HistoryAction.Revert, ["HEAD"]));
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.PrepareAsync(context, HistoryAction.ResetHard, [one]));
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.PrepareAsync(context, HistoryAction.Revert, Enumerable.Range(0, 101).Select(i => i.ToString("x40"))));
        var plan = await HistoryBatch.PrepareAsync(context, HistoryAction.Revert, [one, two]);
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.ExecuteAsync(plan with { Commits = plan.Commits.Reverse().ToArray() }));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index"))); Assert.Equal(refs, repo.Git("show-ref"));
        repo.Write("a", "external"); await Assert.ThrowsAsync<IOException>(() => HistoryBatch.ExecuteAsync(plan));
        Assert.Equal(two, Head(repo)); Assert.Equal("external", File.ReadAllText(Path.Combine(repo.Root, "a")));
    }

    [Fact]
    public async Task Empty_cherry_pick_can_be_skipped_without_losing_remaining_commits()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Write("a", "same"); repo.Commit("same"); var same = Head(repo);
        repo.Write("last", "last"); repo.Commit("last"); var last = Head(repo);
        repo.Git("checkout", "main"); repo.Write("a", "same"); repo.Commit("already applied");
        var plan = await HistoryBatch.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), HistoryAction.CherryPick, [same, last]);
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.ExecuteAsync(plan));
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root); Assert.Equal("cherry-pick", snapshot.Operation?.Kind);
        await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, false, true);
        Assert.Equal("last", File.ReadAllText(Path.Combine(repo.Root, "last"))); Assert.Null((await reader.OpenAsync(repo.Root)).Operation);
    }

    [Fact]
    public async Task Confirmation_context_cannot_skip_a_different_operation_after_external_refresh()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        Assert.Throws<IOException>(() => repo.Git("cherry-pick", "--no-edit", "HEAD"));
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); var captured = model.CaptureOperation()!.Value;
        repo.Git("cherry-pick", "--abort"); repo.Write("a", "changed"); repo.Commit("external"); var external = Head(repo);
        Assert.Throws<IOException>(() => repo.Git("cherry-pick", "--no-edit", "HEAD")); await model.RefreshAsync();
        await model.RunOperationAsync(captured, false, true);
        Assert.Contains("已变化", model.Error); Assert.Equal(external, Head(repo));
        Assert.Equal(external, File.ReadAllText(Path.Combine(repo.Root, ".git", "CHERRY_PICK_HEAD")).Trim());
        Assert.True(model.HasOperation);
    }

    [Fact]
    public async Task Empty_revert_can_be_skipped_and_older_selected_commit_is_still_processed()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        repo.Write("first", "first"); repo.Commit("first"); var first = Head(repo);
        repo.Write("a", "same"); repo.Commit("same"); var same = Head(repo);
        repo.Write("last", "last"); repo.Commit("last"); var last = Head(repo);
        repo.Write("a", "base"); repo.Commit("already undone");
        var plan = await HistoryBatch.PrepareAsync(await RepositoryActions.ReadAsync(repo.Root), HistoryAction.Revert, [same, first, last]);
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.ExecuteAsync(plan));
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root); Assert.Equal("revert", snapshot.Operation?.Kind);
        await new ConflictWriter().OperationAsync(repo.Root, snapshot.IndexToken!, snapshot.Operation!, false, true);
        Assert.False(File.Exists(Path.Combine(repo.Root, "first"))); Assert.False(File.Exists(Path.Combine(repo.Root, "last")));
        Assert.Null((await reader.OpenAsync(repo.Root)).Operation);
    }

    [Fact]
    public async Task Merge_batch_requires_explicit_compatible_mainline_and_preserves_sequence()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base"); var baseline = Head(repo);
        repo.Git("checkout", "-b", "topic"); repo.Write("topic", "topic"); repo.Commit("topic");
        repo.Git("checkout", "main"); repo.Write("main", "main"); repo.Commit("main"); repo.Git("merge", "--no-ff", "--no-edit", "topic"); var merge = Head(repo);
        repo.Write("last", "last"); repo.Commit("last"); var last = Head(repo);
        var context = await RepositoryActions.ReadAsync(repo.Root); var plan = await HistoryBatch.PrepareAsync(context, HistoryAction.Revert, [last, merge]);
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.ExecuteAsync(plan));
        await Assert.ThrowsAsync<IOException>(() => HistoryBatch.PrepareAsync(context, HistoryAction.Revert, [last, merge], 2));
        plan = await HistoryBatch.PrepareAsync(context, HistoryAction.Revert, [merge, last], 1); await HistoryBatch.ExecuteAsync(plan);
        Assert.False(File.Exists(Path.Combine(repo.Root, "topic"))); Assert.False(File.Exists(Path.Combine(repo.Root, "last")));
        Assert.Equal("main", File.ReadAllText(Path.Combine(repo.Root, "main"))); Assert.Equal(baseline, repo.Git("rev-parse", "HEAD~5").Trim());
    }
    private static string Head(TestRepository repo) => repo.Git("rev-parse", "HEAD").Trim();
}
