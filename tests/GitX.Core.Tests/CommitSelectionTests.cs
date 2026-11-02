using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class CommitSelectionTests
{
    private static async Task<CommitFileSelection> Selection(TestRepository repo, string path, params ChangeLine[] lines)
    {
        var request = new DocumentRequest(repo.Root, path, ComparisonKind.LocalChanges);
        using var reader = new RepositoryReader();
        return new(request, await reader.DocumentAsync(request), lines.Length == 0 ? null : lines.ToHashSet());
    }
    private static void Hook(TestRepository repo, string script)
    {
        var path = Path.Combine(repo.Root, ".git/hooks/pre-commit");
        File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + script + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    [Fact]
    public async Task Selected_lines_commit_without_staging_the_rest_or_touching_worktree()
    {
        using var repo = new TestRepository(); repo.Write("a.java", "a\nb\nc\nd\ne\n"); repo.Commit("base");
        const string working = "A\nb\nc\nd\nE\n"; repo.Write("a.java", working);
        var index = repo.Git("write-tree");
        var selection = await Selection(repo, "a.java", new ChangeLine(1, 1));
        Assert.Equal(index, repo.Git("write-tree")); // Capturing a selection never stages it.
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [selection], "selected first line");
        Assert.Null(result.Warning);
        Assert.Equal("A\nb\nc\nd\ne\n", repo.Git("show", "HEAD:a.java"));
        Assert.Equal(working, File.ReadAllText(Path.Combine(repo.Root, "a.java")));
        Assert.Empty(repo.Git("diff", "--cached"));
        Assert.Contains("+E", repo.Git("diff"));
    }
    [Fact]
    public async Task Unselected_staged_rows_in_the_same_file_and_other_files_remain_staged()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "a\nb\nc\nd\ne"); repo.Write("other.txt", "base"); repo.Commit("base");
        repo.Write("a.txt", "staged\nb\nc\nd\ne"); repo.Write("other.txt", "staged other"); repo.Git("add", ".");
        repo.Write("a.txt", "staged\nb\nc\nd\nworking");
        var selection = await Selection(repo, "a.txt", new ChangeLine(1, 5));
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [selection], "only working line");
        Assert.Null(result.Warning);
        Assert.Equal("a\nb\nc\nd\nworking", repo.Git("show", "HEAD:a.txt"));
        Assert.Equal("staged\nb\nc\nd\nworking", repo.Git("show", ":a.txt"));
        Assert.Equal("base", repo.Git("show", "HEAD:other.txt"));
        Assert.Equal("staged other", repo.Git("show", ":other.txt"));
        Assert.Empty(repo.Git("diff", "--", "a.txt"));
    }
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Selected_deletion_and_bom_eof_are_preserved(string newline)
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "\uFEFF" + string.Join(newline, ["a", "remove", "c", "d", "e"])); repo.Commit("base");
        var working = "\uFEFF" + string.Join(newline, ["a", "c", "d", "E"]); repo.Write("a.txt", working);
        await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "a.txt", new ChangeLine(0, 2))], "delete one line");
        Assert.Equal("\uFEFF" + string.Join(newline, ["a", "c", "d", "e"]), "\uFEFF" + repo.Git("show", "HEAD:a.txt").TrimStart('\uFEFF'));
        Assert.Equal(working, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(repo.Root, "a.txt"))));
    }
    [Fact]
    public async Task Partial_new_file_keeps_unselected_staged_additions()
    {
        using var repo = new TestRepository(); repo.Write("base.txt", "base"); repo.Commit("base");
        repo.Write("new.txt", "one\ntwo\n"); repo.Git("add", "new.txt"); repo.Write("new.txt", "one\ntwo\nthree\n");
        await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "new.txt", new(1, 1), new(1, 3))], "selected additions");
        Assert.Equal("one\nthree\n", repo.Git("show", "HEAD:new.txt"));
        Assert.Equal("one\ntwo\nthree\n", repo.Git("show", ":new.txt"));
    }
    [Fact]
    public async Task Whole_file_binary_and_deleted_file_commit_and_leave_unselected_file_alone()
    {
        using var repo = new TestRepository(); repo.Write("delete.txt", "gone"); repo.Write("keep.txt", "keep"); repo.Commit("base");
        File.Delete(Path.Combine(repo.Root, "delete.txt")); File.WriteAllBytes(Path.Combine(repo.Root, "binary.bin"), [0, 1, 2, 255]); repo.Write("keep.txt", "unselected");
        await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "delete.txt"), await Selection(repo, "binary.bin")], "binary and deletion");
        Assert.DoesNotContain("delete.txt", repo.Git("ls-tree", "--name-only", "HEAD"));
        Assert.Equal(repo.Git("hash-object", "binary.bin").Trim(), repo.Git("rev-parse", "HEAD:binary.bin").Trim());
        Assert.Equal("keep", repo.Git("show", "HEAD:keep.txt")); Assert.Empty(repo.Git("diff", "--cached"));
    }
    [Fact]
    public async Task Stale_selection_and_failing_hook_do_not_change_head_or_real_index()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "first");
        var first = await Selection(repo, "a.txt"); var head = repo.Git("rev-parse", "HEAD"); var index = repo.Git("write-tree");
        repo.Write("a.txt", "second");
        await Assert.ThrowsAsync<IOException>(() => new CommitSelectionWriter().CommitAsync(repo.Root, [first], "stale"));
        Hook(repo, "exit 1");
        var current = await Selection(repo, "a.txt");
        await Assert.ThrowsAsync<IOException>(() => new CommitSelectionWriter().CommitAsync(repo.Root, [current], "hook failure"));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("write-tree"));
    }

    [Fact]
    public async Task Existing_real_index_lock_prevents_commit_even_with_a_private_index()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "changed");
        var selection = await Selection(repo, "a.txt"); var head = repo.Git("rev-parse", "HEAD");
        repo.Write(".git/index.lock", "owned by another Git process");
        await Assert.ThrowsAsync<IOException>(() => new CommitSelectionWriter().CommitAsync(repo.Root, [selection], "locked"));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.Equal("owned by another Git process", File.ReadAllText(Path.Combine(repo.Root, ".git/index.lock")));
    }

    [Fact]
    public async Task Hook_added_file_is_reconciled_and_next_cli_commit_keeps_committed_content()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Write("other.txt", "base"); repo.Write("keep.txt", "base"); repo.Commit("base");
        repo.Write("keep.txt", "unselected staged"); repo.Git("add", "keep.txt");
        repo.Write("a.txt", "selected"); repo.Write("other.txt", "hook content");
        var selection = await Selection(repo, "a.txt"); Hook(repo, "git add other.txt");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [selection], "hook tree");
        Assert.Null(result.Warning);
        Assert.Equal(result.Sha, repo.Git("rev-parse", "HEAD").Trim());
        Assert.Equal("hook content", repo.Git("show", "HEAD:other.txt"));
        Assert.Equal("selected", repo.Git("show", ":a.txt"));
        Assert.Equal("hook content", repo.Git("show", ":other.txt"));
        Assert.Equal("keep.txt", repo.Git("diff", "--cached", "--name-only").Trim());
        Assert.Equal("base", repo.Git("show", "HEAD:keep.txt"));
        repo.Git("commit", "-m", "remaining staged without add");
        Assert.Equal("selected", repo.Git("show", "HEAD:a.txt"));
        Assert.Equal("hook content", repo.Git("show", "HEAD:other.txt"));
        Assert.Equal("unselected staged", repo.Git("show", "HEAD:keep.txt"));
        Assert.Empty(repo.Git("status", "--porcelain"));
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git/index.lock")));
    }

    [Fact]
    public async Task Hook_formatted_selected_line_keeps_other_staged_and_unstaged_lines()
    {
        using var repo = new TestRepository();
        var original = Enumerable.Range(1, 30).Select(i => $"line {i:00}").ToArray();
        string Text(string[] lines) => string.Join('\n', lines) + "\n";
        repo.Write("a.txt", Text(original)); repo.Commit("base");
        var staged = original.ToArray(); staged[2] = "staged"; repo.Write("a.txt", Text(staged)); repo.Git("add", "a.txt");
        var working = staged.ToArray(); working[14] = "unselected working"; working[26] = "selected"; repo.Write("a.txt", Text(working));
        Hook(repo, "oid=$(git show :a.txt | sed 's/^selected$/formatted/')\noid=$(printf '%s\\n' \"$oid\" | git hash-object -w --stdin)\ngit update-index --cacheinfo 100644,\"$oid\",a.txt");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "a.txt", new ChangeLine(1, 27))], "format selected line");
        Assert.Null(result.Warning);
        var committed = original.ToArray(); committed[26] = "formatted";
        var remaining = staged.ToArray(); remaining[26] = "formatted";
        Assert.Equal(Text(committed), repo.Git("show", "HEAD:a.txt"));
        Assert.Equal(Text(remaining), repo.Git("show", ":a.txt"));
        Assert.Equal(Text(working), File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        repo.Git("commit", "-m", "remaining staged without add");
        Assert.Equal(Text(remaining), repo.Git("show", "HEAD:a.txt"));
        Assert.Contains("+unselected working", repo.Git("diff"));
    }

    [Fact]
    public async Task Hook_binary_addition_and_deletion_do_not_leave_reverse_staging()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Write("delete.txt", "gone"); repo.Commit("base");
        repo.Write("a.txt", "selected"); File.Delete(Path.Combine(repo.Root, "delete.txt"));
        File.WriteAllBytes(Path.Combine(repo.Root, "extra file ü.bin"), [0, 255, 2, 3]);
        Hook(repo, "git add -A -- 'extra file ü.bin' delete.txt");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "a.txt")], "hook paths");
        Assert.Null(result.Warning);
        Assert.Empty(repo.Git("diff", "--cached")); Assert.Empty(repo.Git("status", "--porcelain"));
        Assert.Equal(repo.Git("hash-object", "extra file ü.bin").Trim(), repo.Git("rev-parse", "HEAD:extra file ü.bin").Trim());
        Assert.DoesNotContain("delete.txt", repo.Git("ls-tree", "--name-only", "HEAD"));
    }

    [Fact]
    public async Task Hook_reconciliation_preserves_unrelated_intent_to_add_and_skip_worktree_flags()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Write("keep.txt", "base"); repo.Write("other.txt", "base"); repo.Commit("base");
        repo.Write("intent.txt", "not staged"); repo.Git("add", "-N", "intent.txt"); repo.Git("update-index", "--skip-worktree", "keep.txt");
        var intent = repo.Git("ls-files", "--debug", "--", "intent.txt");
        repo.Write("a.txt", "selected"); repo.Write("other.txt", "hook content"); Hook(repo, "git add other.txt");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "a.txt")], "hook with index flags");
        Assert.Null(result.Warning); Assert.Empty(repo.Git("diff", "--cached"));
        Assert.Equal(intent, repo.Git("ls-files", "--debug", "--", "intent.txt"));
        Assert.StartsWith("S ", repo.Git("ls-files", "-v", "--", "keep.txt"));
        Assert.Contains(" A intent.txt", repo.Git("status", "--porcelain"));
        Assert.Equal("not staged", File.ReadAllText(Path.Combine(repo.Root, "intent.txt")));
    }

    [Fact]
    public async Task Selected_line_changed_after_staging_is_not_left_staged_in_reverse()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base\nkeep\n"); repo.Commit("base");
        repo.Write("a.txt", "staged\nkeep\n"); repo.Git("add", "a.txt"); repo.Write("a.txt", "working\nkeep\n");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "a.txt", new ChangeLine(1, 1))], "selected overlapping line");
        Assert.Null(result.Warning); Assert.Empty(repo.Git("diff", "--cached"));
        Assert.Equal("working\nkeep\n", repo.Git("show", "HEAD:a.txt"));
        Assert.Equal("working\nkeep\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task Hook_conflicting_with_remaining_staging_keeps_index_and_recovery_with_clear_warning()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Write("other.txt", "base"); repo.Commit("base");
        repo.Write("other.txt", "unselected staged"); repo.Git("add", "other.txt");
        repo.Write("other.txt", "hook content"); repo.Write("a.txt", "selected");
        var index = repo.Git("write-tree"); Hook(repo, "git add other.txt");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "a.txt")], "conflicting hook");
        Assert.Contains("未同步索引", result.Warning); Assert.Contains("反向暂存", result.Warning); Assert.Contains("不要直接提交", result.Warning);
        Assert.Equal(index, repo.Git("write-tree")); Assert.Equal("selected", repo.Git("show", "HEAD:a.txt"));
        Assert.Equal("hook content", repo.Git("show", "HEAD:other.txt"));
        Assert.Equal("hook content", File.ReadAllText(Path.Combine(repo.Root, "other.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(repo.Root, ".git/gitx-recovery"), "original.index", SearchOption.AllDirectories));
        Assert.Empty(repo.Git("ls-files", "-u")); Assert.False(File.Exists(Path.Combine(repo.Root, ".git/index.lock")));
    }

    [Fact]
    public async Task Hook_external_index_change_is_not_overwritten_after_commit()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Write("other.txt", "base"); repo.Commit("base");
        repo.Write("a.txt", "selected"); repo.Write("other.txt", "external staged");
        Hook(repo, "GIT_INDEX_FILE=\"$(git rev-parse --absolute-git-dir)/index\" git add other.txt");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "a.txt")], "external index");
        Assert.Contains("索引被外部进程修改", result.Warning);
        Assert.Equal("selected", repo.Git("show", "HEAD:a.txt")); Assert.Equal("base", repo.Git("show", ":a.txt"));
        Assert.Equal("external staged", repo.Git("show", ":other.txt")); Assert.False(File.Exists(Path.Combine(repo.Root, ".git/index.lock")));
    }

    [Fact]
    public async Task Hook_created_real_index_lock_is_preserved_after_commit()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "selected");
        var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git/index")); Hook(repo, "printf 'other owner' > \"$(git rev-parse --absolute-git-dir)/index.lock\"");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo, "a.txt")], "lock during hook");
        Assert.Contains("未同步索引", result.Warning); Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git/index")));
        Assert.Equal("selected", repo.Git("show", "HEAD:a.txt")); Assert.Equal("other owner", File.ReadAllText(Path.Combine(repo.Root, ".git/index.lock")));
    }
}
