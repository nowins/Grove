using System.Text;
using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryFileActionTests
{
    [Fact]
    public async Task Patch_preserves_non_utf8_text_bytes_in_added_files()
    {
        using var repo = new TestRepository();
        byte[] content = [0x63, 0x61, 0x66, 0xe9, 0x0a];
        repo.Write("keep.txt", "base\n"); File.WriteAllBytes(Path.Combine(repo.Root, "existing.txt"), content);
        repo.Commit("base"); var baseline = Head(repo);
        byte[] changed = [0x63, 0x61, 0x66, 0xe8, 0x0a];
        File.WriteAllBytes(Path.Combine(repo.Root, "existing.txt"), changed);
        File.WriteAllBytes(Path.Combine(repo.Root, "latin1.txt"), content); repo.Commit("add non UTF-8 text");
        var selection = await HistoryFileActions.ReadAsync(repo.Root, Head(repo), ["latin1.txt", "existing.txt"]);
        Assert.Contains((byte)0xe9, await HistoryFileActions.PatchAsync(selection));
        repo.Git("reset", "--hard", baseline);
        var recovery = await HistoryFileActions.ExecuteAsync(await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.CherryPick));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(repo.Root, "latin1.txt")));
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(repo.Root, "existing.txt")));
        Assert.Equal(baseline, Head(repo)); Assert.Equal("", repo.Git("diff", "--cached"));
        await new RepositoryWriter().RestoreAsync(recovery);
        Assert.False(File.Exists(Path.Combine(repo.Root, "latin1.txt")));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(repo.Root, "existing.txt")));
    }

    [Fact]
    public async Task Selected_patch_applies_only_selected_changes_preserving_local_edits_index_head_and_undo()
    {
        using var repo = new TestRepository();
        var original = Lines(); repo.Write("a.txt", original); repo.Write("other.txt", "before\n"); repo.Write("staged.txt", "base\n"); repo.Commit("base");
        var baseline = Head(repo);
        repo.Write("a.txt", original.Replace("line 5\n", "selected change\n")); repo.Write("other.txt", "unselected commit change\n"); repo.Commit("change two files");
        var selection = await HistoryFileActions.ReadAsync(repo.Root, Head(repo), ["a.txt"]);
        var patch = Encoding.UTF8.GetString(await HistoryFileActions.PatchAsync(selection));
        Assert.Contains("selected change", patch); Assert.DoesNotContain("unselected commit change", patch);
        repo.Git("reset", "--hard", baseline);
        repo.Write("a.txt", original + "local tail\n"); repo.Write("staged.txt", "staged edit\n"); repo.Git("add", "staged.txt");
        var index = repo.Git("diff", "--cached", "--binary");
        var plan = await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.CherryPick);
        var recovery = await HistoryFileActions.ExecuteAsync(plan);
        Assert.Equal(original.Replace("line 5\n", "selected change\n") + "local tail\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal("before\n", File.ReadAllText(Path.Combine(repo.Root, "other.txt")));
        Assert.Equal(baseline, Head(repo)); Assert.Equal(index, repo.Git("diff", "--cached", "--binary"));
        await new RepositoryWriter().RestoreAsync(recovery);
        Assert.Equal(original + "local tail\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal(index, repo.Git("diff", "--cached", "--binary"));
    }

    [Fact]
    public async Task Revert_add_delete_binary_and_unicode_paths_are_worktree_only_and_recoverable()
    {
        using var repo = new TestRepository(); repo.Write("gone.txt", "keep deleted original\n");
        var binary = new byte[] { 0, 1, 255, 2, 0, 3 }; File.WriteAllBytes(Path.Combine(repo.Root, "data.bin"), binary); repo.Commit("base");
        repo.Git("rm", "gone.txt"); repo.Write("-中文 [x].txt", "new file\n");
        var changed = new byte[] { 0, 5, 4, 255, 0, 6 }; File.WriteAllBytes(Path.Combine(repo.Root, "data.bin"), changed); repo.Commit("mixed");
        var head = Head(repo); var tree = repo.Git("write-tree");
        var selection = await HistoryFileActions.ReadAsync(repo.Root, head, ["gone.txt", "-中文 [x].txt", "data.bin"]);
        Assert.Contains("GIT binary patch", Encoding.UTF8.GetString(await HistoryFileActions.PatchAsync(selection)));
        var recovery = await HistoryFileActions.ExecuteAsync(await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.Revert));
        Assert.Equal("keep deleted original\n", File.ReadAllText(Path.Combine(repo.Root, "gone.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "-中文 [x].txt"))); Assert.Equal(binary, File.ReadAllBytes(Path.Combine(repo.Root, "data.bin")));
        Assert.Equal(head, Head(repo)); Assert.Equal(tree, repo.Git("write-tree"));
        await new RepositoryWriter().RestoreAsync(recovery);
        Assert.False(File.Exists(Path.Combine(repo.Root, "gone.txt"))); Assert.Equal(changed, File.ReadAllBytes(Path.Combine(repo.Root, "data.bin")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "-中文 [x].txt")));
    }

    [Fact]
    public async Task Root_commit_patch_and_before_local_comparison_use_an_empty_before_version()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "root\r\n"); repo.Commit("root"); var root = Head(repo);
        var selection = await HistoryFileActions.ReadAsync(repo.Root, root, ["a.txt"]);
        Assert.Null(selection.Parent); Assert.Contains("new file mode", Encoding.UTF8.GetString(await HistoryFileActions.PatchAsync(selection)));
        using var reader = new RepositoryReader();
        var before = await reader.DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.CommitParentWorktree, root));
        Assert.False(before.Diff!.Old.Exists); Assert.Equal("root\r\n", before.Diff.New.Text); Assert.False(before.CanStage);
        var recovery = await HistoryFileActions.ExecuteAsync(await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.Revert));
        Assert.False(File.Exists(Path.Combine(repo.Root, "a.txt")));
        await new RepositoryWriter().RestoreAsync(recovery); Assert.Equal("root\r\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task Get_revision_replaces_only_selected_worktree_bytes_and_deleted_revision_can_be_undone()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "first\n"); repo.Write("b.txt", "base\n"); repo.Commit("first"); var first = Head(repo);
        repo.Git("rm", "a.txt"); repo.Write("b.txt", "second\n"); repo.Commit("delete a"); var deleted = Head(repo);
        repo.Write("a.txt", "untracked replacement\r\n"); repo.Write("b.txt", "staged b\n"); repo.Git("add", "b.txt");
        var index = repo.Git("diff", "--cached", "--binary");
        var selection = await HistoryFileActions.ReadAsync(repo.Root, first, ["a.txt"]);
        var recovery = await HistoryFileActions.ExecuteAsync(await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.GetRevision));
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt"))); Assert.Equal(deleted, Head(repo));
        Assert.Equal(index, repo.Git("diff", "--cached", "--binary"));
        await new RepositoryWriter().RestoreAsync(recovery); Assert.Equal("untracked replacement\r\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        var delete = await HistoryFileActions.ReadAsync(repo.Root, deleted, ["a.txt"]);
        recovery = await HistoryFileActions.ExecuteAsync(await HistoryFileActions.PrepareAsync(delete, HistoryFileOperation.GetRevision));
        Assert.False(File.Exists(Path.Combine(repo.Root, "a.txt"))); Assert.Equal(index, repo.Git("diff", "--cached", "--binary"));
        await new RepositoryWriter().RestoreAsync(recovery); Assert.Equal("untracked replacement\r\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task Overlapping_patch_and_stale_confirmation_fail_without_overwriting_worktree_or_index()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "first\n"); repo.Commit("base"); var baseline = Head(repo);
        repo.Write("a.txt", "second\n"); repo.Commit("change"); var selection = await HistoryFileActions.ReadAsync(repo.Root, Head(repo), ["a.txt"]);
        repo.Git("reset", "--hard", baseline); repo.Write("a.txt", "conflicting local\n");
        var index = repo.Git("write-tree");
        var conflict = await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.CherryPick);
        await Assert.ThrowsAsync<IOException>(() => HistoryFileActions.ExecuteAsync(conflict));
        Assert.Equal("conflicting local\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt"))); Assert.Equal(index, repo.Git("write-tree"));
        var plan = await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.GetRevision);
        repo.Write("a.txt", "changed after confirm\n");
        await Assert.ThrowsAsync<IOException>(() => HistoryFileActions.ExecuteAsync(plan));
        Assert.Equal("changed after confirm\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt"))); Assert.Equal(baseline, Head(repo));
    }

    [Fact]
    public async Task Selected_commit_is_fixed_and_history_up_to_here_excludes_later_and_other_branch_commits()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "first\n"); repo.Commit("first"); var first = Head(repo);
        repo.Write("a.cs", "second\n"); repo.Commit("second"); var second = Head(repo);
        var selection = await HistoryFileActions.ReadAsync(repo.Root, "main", ["a.cs"]);
        repo.Write("a.cs", "third\n"); repo.Commit("third");
        using var history = new FileHistoryViewModel(new(repo.Root, "a.cs", second)); await history.RefreshAsync();
        Assert.Equal(new[] { "second", "first" }, history.Commits.Select(c => c.Subject));
        Assert.Equal(second, selection.Commit); Assert.Contains("+second", Encoding.UTF8.GetString(await HistoryFileActions.PatchAsync(selection)));
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); await model.OpenRevisionSourceAsync("a.cs", first);
        Assert.Equal("first\n", model.CurrentDocument!.Text); Assert.Equal(first, model.CurrentDocument.SourceRevision);
        Assert.Equal(0, model.Mode); Assert.False(model.CurrentDocument.CanStage);
        var tab = model.ActiveSourceTabId;
        await model.OpenRevisionSourceAsync("a.cs", first); Assert.Equal(tab, model.ActiveSourceTabId); Assert.Single(model.SourceTabs);
    }

    [Fact]
    public async Task Merge_parent_comparisons_keep_both_parent_versions_distinct()
    {
        using var repo = new TestRepository(); var original = Lines(); repo.Write("a.txt", original); repo.Commit("base");
        repo.Git("checkout", "-b", "side"); repo.Write("a.txt", original.Replace("line 2\n", "side edit\n")); repo.Commit("side"); var side = Head(repo);
        repo.Git("checkout", "main"); repo.Write("a.txt", original.Replace("line 30\n", "main edit\n")); repo.Commit("main"); var main = Head(repo);
        repo.Git("merge", "--no-ff", "side", "-m", "merge"); var merged = Head(repo);
        var selection = await HistoryFileActions.ReadAsync(repo.Root, merged, ["a.txt"]); Assert.Equal(new[] { main, side }, selection.Parents);
        using var reader = new RepositoryReader();
        var first = await reader.DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.RevisionRange, merged, BaseCommit: main));
        var second = await reader.DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.RevisionRange, merged, BaseCommit: side));
        Assert.NotEqual(first.Diff!.Old.Text, second.Diff!.Old.Text); Assert.Equal(first.Diff.New.Text, second.Diff.New.Text);
        var before = await reader.DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.CommitParentWorktree, merged));
        Assert.Equal(first.Diff.Old.Text, before.Diff!.Old.Text);
    }

    [Fact]
    public async Task Unselected_paths_custom_filters_and_special_tree_types_are_rejected()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "first\n"); repo.Commit("base");
        await Assert.ThrowsAsync<IOException>(() => HistoryFileActions.ReadAsync(repo.Root, Head(repo), ["../outside"]));
        var selection = await HistoryFileActions.ReadAsync(repo.Root, Head(repo), ["a.txt"]);
        repo.Write(".gitattributes", "a.txt filter=external\n");
        await Assert.ThrowsAsync<IOException>(() => HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.GetRevision));
        File.Delete(Path.Combine(repo.Root, ".gitattributes"));
        var blob = repo.Git("hash-object", "-w", "a.txt").Trim(); repo.Git("update-index", "--add", "--cacheinfo", "120000", blob, "link"); repo.Git("commit", "-m", "link");
        var link = await HistoryFileActions.ReadAsync(repo.Root, Head(repo), ["link"]);
        await Assert.ThrowsAsync<IOException>(() => HistoryFileActions.PrepareAsync(link, HistoryFileOperation.GetRevision));
    }

    private static string Head(TestRepository repo) => repo.Git("rev-parse", "HEAD").Trim();
    private static string Lines() => string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i}\n"));

    [Fact]
    public async Task Autocrlf_and_missing_final_newline_preserve_checkout_bytes_when_applying_changes()
    {
        using var repo = new TestRepository(); repo.Git("config", "core.autocrlf", "true");
        repo.Write("a.txt", "one\r\ntwo"); repo.Commit("base"); var baseline = Head(repo);
        repo.Write("a.txt", "one\r\nchanged"); repo.Commit("change"); var selection = await HistoryFileActions.ReadAsync(repo.Root, Head(repo), ["a.txt"]);
        repo.Git("reset", "--hard", baseline);
        await HistoryFileActions.ExecuteAsync(await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.CherryPick));
        Assert.Equal("one\r\nchanged", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal(baseline, Head(repo)); Assert.Empty(repo.Git("diff", "--cached"));
    }

    [Fact]
    public async Task Rename_pair_applies_and_undoes_both_paths_without_touching_index()
    {
        using var repo = new TestRepository(); repo.Write("old.txt", "original\n"); repo.Commit("base"); var baseline = Head(repo);
        repo.Git("mv", "old.txt", "new.txt"); repo.Commit("rename");
        var selection = await HistoryFileActions.ReadAsync(repo.Root, Head(repo), ["old.txt", "new.txt"]);
        repo.Git("reset", "--hard", baseline);
        var recovery = await HistoryFileActions.ExecuteAsync(await HistoryFileActions.PrepareAsync(selection, HistoryFileOperation.CherryPick));
        Assert.False(File.Exists(Path.Combine(repo.Root, "old.txt"))); Assert.Equal("original\n", File.ReadAllText(Path.Combine(repo.Root, "new.txt")));
        Assert.Empty(repo.Git("diff", "--cached"));
        await new RepositoryWriter().RestoreAsync(recovery);
        Assert.True(File.Exists(Path.Combine(repo.Root, "old.txt"))); Assert.False(File.Exists(Path.Combine(repo.Root, "new.txt")));
    }
}
