using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RepositoryWriterTests
{
    private readonly RepositoryReader _reader = new();
    private readonly RepositoryWriter _writer = new();
    private async Task FileAction(TestRepository repo, string path, ComparisonKind kind, FileWriteOperation action, string? old = null)
    {
        var request = new DocumentRequest(repo.Root, path, kind, OldPath: old);
        var document = await _reader.DocumentAsync(request);
        await _writer.FileAsync(request, document.WriteToken!, action);
    }

    [Fact]
    public async Task Stage_and_unstage_on_unborn_head_preserve_worktree_and_binary()
    {
        using var repo = new TestRepository();
        repo.Write("空 文件.txt", "");
        await FileAction(repo, "空 文件.txt", ComparisonKind.Worktree, FileWriteOperation.Stage);
        Assert.Contains("空 文件.txt", repo.Git("ls-files", "-z"));
        await FileAction(repo, "空 文件.txt", ComparisonKind.Index, FileWriteOperation.Unstage);
        Assert.Empty(repo.Git("ls-files")); Assert.True(File.Exists(Path.Combine(repo.Root, "空 文件.txt")));
        File.WriteAllBytes(Path.Combine(repo.Root, "binary.bin"), [0, 1, 2, 255]);
        await FileAction(repo, "binary.bin", ComparisonKind.Worktree, FileWriteOperation.Stage);
        Assert.Contains("binary.bin", repo.Git("diff", "--cached", "--name-only"));
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(Path.Combine(repo.Root, "binary.bin")));
    }

    [Fact]
    public async Task File_operations_keep_partial_stage_and_handle_rename_and_deletion()
    {
        using var repo = new TestRepository(); repo.Write("old.txt", "base\n"); repo.Commit("base");
        repo.Git("mv", "old.txt", "new.txt");
        await FileAction(repo, "new.txt", ComparisonKind.Index, FileWriteOperation.Unstage, "old.txt");
        Assert.Equal("base\n", repo.Git("show", ":old.txt"));
        Assert.True(File.Exists(Path.Combine(repo.Root, "new.txt")));
        await FileAction(repo, "old.txt", ComparisonKind.Worktree, FileWriteOperation.Stage);
        await FileAction(repo, "new.txt", ComparisonKind.Worktree, FileWriteOperation.Stage);
        repo.Write("new.txt", "unstaged\n");
        await FileAction(repo, "new.txt", ComparisonKind.Index, FileWriteOperation.Unstage, "old.txt");
        Assert.Equal("unstaged\n", File.ReadAllText(Path.Combine(repo.Root, "new.txt")));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Selected_line_preserves_existing_index_changes_and_other_worktree_changes(string newline)
    {
        using var repo = new TestRepository();
        if (newline == "\r\n") { repo.Write(".gitattributes", "*.txt text eol=crlf\n"); }
        var original = Enumerable.Range(1, 20).Select(i => $"value {i}").ToArray();
        repo.Write("a.txt", string.Join(newline, original)); repo.Commit("base");
        var staged = original.ToArray(); staged[1] = "value 200";
        repo.Write("a.txt", string.Join(newline, staged)); repo.Git("add", "a.txt");
        var working = staged.ToArray(); working[7] = "value 800"; working[8] = "value 900"; working[14] = "value 1500";
        var content = string.Join(newline, working); repo.Write("a.txt", content);
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.Worktree);
        var document = await _reader.DocumentAsync(request);
        await _writer.StageSelectionAsync(request, document, new(null, 1, 8, 8));
        var expected = staged.ToArray(); expected[7] = "value 800";
        var indexText = repo.Git("show", ":a.txt").Replace("\r\n", "\n");
        Assert.Equal(string.Join("\n", expected), indexText);
        Assert.Equal(content, File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Contains("value 900", repo.Git("diff", "--", "a.txt"));
        Assert.DoesNotContain("+value 800", repo.Git("diff", "--", "a.txt"));
    }

    [Fact]
    public async Task Partial_deletion_and_new_file_keep_eof_and_do_not_touch_worktree()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "keep\nremove\nlast"); repo.Commit("base");
        repo.Write("a.txt", "keep\nlast");
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.Worktree);
        await _writer.StageSelectionAsync(request, await _reader.DocumentAsync(request), new(null, 0, 2, 2));
        Assert.Equal("keep\nlast", repo.Git("show", ":a.txt"));
        repo.Write("new.txt", "one\ntwo\nthree"); request = new(repo.Root, "new.txt", ComparisonKind.Worktree);
        await _writer.StageSelectionAsync(request, await _reader.DocumentAsync(request), new(null, 1, 2, 2));
        Assert.Equal("two\n", repo.Git("show", ":new.txt"));
        Assert.Equal("one\ntwo\nthree", File.ReadAllText(Path.Combine(repo.Root, "new.txt")));
    }

    [Fact]
    public async Task Staging_one_row_with_different_eol_preserves_other_index_and_worktree_bytes()
    {
        using var repo = new TestRepository(); repo.Git("config", "core.autocrlf", "false");
        repo.Write("a.txt", "a\nb\nc\nd\ne"); repo.Commit("base");
        repo.Write("a.txt", "staged\nb\nc\nd\ne"); repo.Git("add", "a.txt");
        const string working = "staged\r\nchanged\r\nc\r\nd\r\nremaining"; repo.Write("a.txt", working);
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.Worktree);
        await _writer.StageSelectionAsync(request, await _reader.DocumentAsync(request), new(null, 1, 2, 2));
        Assert.Equal("staged\nchanged\r\nc\nd\ne", repo.Git("show", ":a.txt"));
        Assert.Equal(working, File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal("混合 LF/CRLF", (await _reader.DocumentAsync(request)).Diff!.Old.LineEnding);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Block_revert_keeps_staged_and_other_worktree_changes_and_can_be_undone(string newline)
    {
        using var repo = new TestRepository(); repo.Git("config", "core.autocrlf", "false");
        repo.Write("a.txt", "a\nb\nc\nd\ne\nf"); repo.Commit("base");
        repo.Write("a.txt", "staged\nb\nc\nd\ne\nf"); repo.Git("add", "a.txt");
        var working = "\uFEFF" + string.Join(newline, new[] { "staged", "changed", "c", "d", "e", "remaining" }); repo.Write("a.txt", working);
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.Worktree);
        var document = await _reader.DocumentAsync(request);
        var index = repo.Git("rev-parse", ":a.txt");
        var backup = await _writer.RevertBlockAsync(request, document, 0);
        Assert.Equal("\uFEFF" + string.Join(newline, new[] { "staged", "b", "c", "d", "e", "remaining" }), System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(repo.Root, "a.txt"))));
        Assert.Equal(index, repo.Git("rev-parse", ":a.txt"));
        await Assert.ThrowsAsync<IOException>(() => _writer.RevertBlockAsync(request, document, 0));
        await _writer.RestoreAsync(backup);
        Assert.Equal(working, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(repo.Root, "a.txt"))));
    }

    [Theory]
    [InlineData("a\nb\nc", "a\nnew\nb\nc", "a\nb\nc")]
    [InlineData("a\nb\nc", "a\nc", "a\nb\nc")]
    [InlineData("a\nb\nc", "a\nb", "a\nb\nc")]
    [InlineData("a\nb\n", "a\nb", "a\nb\n")]
    [InlineData("a\nb", "a\nb\n", "a\nb")]
    [InlineData("a\n", "", "a\n")]
    [InlineData("a\r\n", "", "a\r\n")]
    public async Task Block_revert_restores_insertions_deletions_and_exact_eof(string before, string after, string expected)
    {
        using var repo = new TestRepository(); repo.Write("a.txt", before); repo.Commit("base"); repo.Write("a.txt", after);
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.Worktree);
        await _writer.RevertBlockAsync(request, await _reader.DocumentAsync(request), 0);
        Assert.Equal(expected, File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Empty(repo.Git("diff", "--cached"));
    }

    [Fact]
    public async Task Discard_is_recoverable_and_preserves_index_including_deleted_and_untracked_files()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base\n"); repo.Commit("base");
        repo.Write("a.txt", "staged\n"); repo.Git("add", "a.txt"); repo.Write("a.txt", "working\n");
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.Worktree);
        var backup = await _writer.FileAsync(request, (await _reader.DocumentAsync(request)).WriteToken!, FileWriteOperation.Discard);
        Assert.Equal("staged\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        await _writer.RestoreAsync(backup!);
        Assert.Equal("working\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal("staged\n", repo.Git("show", ":a.txt"));
        File.Delete(Path.Combine(repo.Root, "a.txt"));
        backup = await _writer.FileAsync(request, (await _reader.DocumentAsync(request)).WriteToken!, FileWriteOperation.Discard);
        Assert.True(File.Exists(Path.Combine(repo.Root, "a.txt")));
        await _writer.RestoreAsync(backup!); Assert.False(File.Exists(Path.Combine(repo.Root, "a.txt")));
        repo.Write("untracked.txt", "recover me"); request = new(repo.Root, "untracked.txt", ComparisonKind.Worktree);
        backup = await _writer.FileAsync(request, (await _reader.DocumentAsync(request)).WriteToken!, FileWriteOperation.Discard);
        Assert.False(File.Exists(Path.Combine(repo.Root, "untracked.txt")));
        await _writer.RestoreAsync(backup!); Assert.Equal("recover me", File.ReadAllText(Path.Combine(repo.Root, "untracked.txt")));
    }

    [Fact]
    public async Task Stale_snapshot_and_existing_index_lock_fail_without_overwriting_index()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "first");
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.Worktree);
        var document = await _reader.DocumentAsync(request);
        repo.Write("a.txt", "second");
        await Assert.ThrowsAsync<IOException>(() => _writer.FileAsync(request, document.WriteToken!, FileWriteOperation.Stage));
        Assert.Equal("base", repo.Git("show", ":a.txt"));
        document = await _reader.DocumentAsync(request);
        var lockPath = Path.Combine(repo.Root, ".git", "index.lock"); File.WriteAllText(lockPath, "owned by test");
        try { await Assert.ThrowsAsync<IOException>(() => _writer.FileAsync(request, document.WriteToken!, FileWriteOperation.Stage)); Assert.True(File.Exists(lockPath)); }
        finally { File.Delete(lockPath); }
        Assert.Equal("base", repo.Git("show", ":a.txt"));
    }

    [Fact]
    public async Task Commit_uses_only_index_and_preserves_unstaged_changes_and_hook_failures()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "staged"); repo.Git("add", "a.txt"); repo.Write("a.txt", "unstaged");
        await _writer.CommitAsync(repo.Root, RepositoryState.Token(repo.Root, []), "中文提交\n\n说明正文");
        Assert.Equal("staged", repo.Git("show", "HEAD:a.txt"));
        Assert.Equal("unstaged", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        repo.Git("add", "a.txt"); var head = repo.Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(repo.Root, ".git", "hooks", "pre-commit"), "#!/bin/sh\necho expected-hook-failure >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(repo.Root, ".git/hooks/pre-commit"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var error = await Assert.ThrowsAsync<IOException>(() => _writer.CommitAsync(repo.Root, RepositoryState.Token(repo.Root, []), "must fail"));
        Assert.Contains("expected-hook-failure", error.Message); Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.Contains("a.txt", repo.Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task Commit_hooks_keep_normal_git_pathspec_semantics()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "before"); repo.Commit("base");
        repo.Write("a.cs", "after"); repo.Git("add", "a.cs");
        var hook = Path.Combine(repo.Root, ".git/hooks/pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\ngit ls-files -- '*.cs' > .git/hook-paths\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await _writer.CommitAsync(repo.Root, RepositoryState.Token(repo.Root, []), "hook glob");
        Assert.Equal("a.cs\n", File.ReadAllText(Path.Combine(repo.Root, ".git/hook-paths")).Replace("\r\n", "\n"));
        Assert.Equal("after", repo.Git("show", "HEAD:a.cs"));
    }

    [Theory]
    [InlineData("one\n", "one")]
    [InlineData("one", "one\n")]
    [InlineData("one", "one\ntwo")]
    [InlineData("one\n", "")]
    public async Task Whole_block_keeps_bom_and_exact_eof(string before, string after)
    {
        using var repo = new TestRepository();
        repo.Write("bom.txt", "\uFEFF" + before); repo.Commit("base"); repo.Write("bom.txt", "\uFEFF" + after);
        var request = new DocumentRequest(repo.Root, "bom.txt", ComparisonKind.Worktree);
        await _writer.StageSelectionAsync(request, await _reader.DocumentAsync(request), new(0));
        // Compare blob ids to include the BOM and newline bytes, without console decoding.
        Assert.Equal(repo.Git("hash-object", "bom.txt").Trim(), repo.Git("rev-parse", ":bom.txt").Trim());
        Assert.Equal("\uFEFF" + after, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(repo.Root, "bom.txt"))));
    }

    [Fact]
    public async Task Queued_writes_recheck_index_and_cannot_overwrite_a_newer_stage()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "working");
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.Worktree);
        var document = await _reader.DocumentAsync(request);
        var first = _writer.FileAsync(request, document.WriteToken!, FileWriteOperation.Stage);
        var second = _writer.FileAsync(request, document.WriteToken!, FileWriteOperation.Stage);
        await first;
        await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Equal("working", repo.Git("show", ":a.txt"));
    }
}
