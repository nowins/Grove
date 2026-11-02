using System.Diagnostics;
using System.Text;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RepositoryReaderTests
{
    private readonly RepositoryReader _reader = new();

    [Fact]
    public async Task History_references_expose_branches_tags_and_full_message_without_checkout()
    {
        using var repo = new TestRepository();
        repo.Write("src/one.txt", "base"); repo.Commit("base\n\nDetailed body.");
        var main = repo.Git("rev-parse", "HEAD").Trim();
        repo.Git("tag", "v1"); repo.Git("update-ref", "refs/remotes/origin/main", main);
        repo.Git("switch", "-c", "feature"); repo.Write("src/two.txt", "feature"); repo.Commit("feature only");
        var feature = repo.Git("rev-parse", "HEAD").Trim(); repo.Git("switch", "main");
        var snapshot = await _reader.OpenAsync(repo.Root);
        Assert.Contains(snapshot.References!, r => r.CanonicalName == "refs/heads/main" && r.Current && r.Kind == "local" && r.Sha == main);
        Assert.Contains(snapshot.References!, r => r.CanonicalName == "refs/heads/feature" && !r.Current && r.Sha == feature);
        Assert.Contains(snapshot.References!, r => r.CanonicalName == "refs/remotes/origin/main" && r.Kind == "remote");
        Assert.Contains(snapshot.References!, r => r.CanonicalName == "refs/tags/v1" && r.Kind == "tag");
        var selected = await _reader.HistoryAsync(repo.Root, main);
        Assert.Equal("base\n\nDetailed body.", Assert.Single(selected.Commits).FullMessage);
        Assert.Equal(2, (await _reader.HistoryAsync(repo.Root, feature)).Commits.Count);
        Assert.Equal(main, repo.Git("rev-parse", "HEAD").Trim());
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
    }

    [Fact]
    public async Task Unborn_repository_has_files_and_separate_staged_and_untracked_entries()
    {
        using var repo = new TestRepository();
        repo.Write("已暂存.cs", "class First {}\n");
        repo.Git("add", "--", "已暂存.cs");
        repo.Write("未跟踪.txt", "你好 GitX");
        repo.Write(".gitignore", "ignored/\n");
        repo.Write("ignored/cache.txt", "not part of the project");
        var snapshot = await _reader.OpenAsync(repo.Root);
        Assert.Null(snapshot.Head);
        Assert.Contains(snapshot.Changes, c => c.Path == "已暂存.cs" && c.Kind == ComparisonKind.Index);
        Assert.Contains(snapshot.Changes, c => c.Path == "未跟踪.txt" && c.Kind == ComparisonKind.Worktree);
        Assert.Contains(snapshot.Changes, c => c.Path == "未跟踪.txt" && c.Status == "未跟踪");
        Assert.Contains(snapshot.Changes, c => c.Path == "已暂存.cs" && c.Status == "新增");
        Assert.DoesNotContain(snapshot.Files, f => f.Path.StartsWith("ignored/"));
        Assert.Empty((await _reader.HistoryAsync(repo.Root, snapshot.Head)).Commits);
        var staged = await _reader.DocumentAsync(new(repo.Root, "已暂存.cs", ComparisonKind.Index));
        Assert.Contains("+class First {}", staged.Text);
        var untracked = await _reader.DocumentAsync(new(repo.Root, "未跟踪.txt", ComparisonKind.Worktree));
        Assert.Equal("你好 GitX", untracked.Diff!.New.Text);
        Assert.False(untracked.Diff.Old.Exists);
        Assert.Contains("未跟踪", untracked.Comparison);
    }

    [Fact]
    public async Task Ignore_rules_follow_git_and_keep_already_tracked_files_visible()
    {
        using var repo = new TestRepository();
        repo.Write(".gitignore", ".cache/  # not a gitignore comment\n");
        repo.Write(".cache/wheels/untracked.whl", "cache");
        var before = await _reader.OpenAsync(repo.Root);
        Assert.Contains(before.Changes, change => change.Path == ".cache/wheels/untracked.whl" && change.Status == "未跟踪");
        repo.Write(".gitignore", "# cache description\n.cache/\n");
        repo.Write(".cache/tracked.txt", "tracked"); repo.Git("add", "-f", "--", ".cache/tracked.txt");
        repo.Git("add", "--", ".gitignore"); repo.Git("commit", "-m", "tracked cache exception");
        repo.Write(".cache/tracked.txt", "changed tracked content");
        var index = repo.Git("ls-files", "--stage");
        var after = await _reader.OpenAsync(repo.Root);
        Assert.DoesNotContain(after.Files, file => file.Path == ".cache/wheels/untracked.whl");
        Assert.DoesNotContain(after.Changes, change => change.Path == ".cache/wheels/untracked.whl");
        Assert.Contains(after.Changes, change => change.Path == ".cache/tracked.txt");
        Assert.Equal(index, repo.Git("ls-files", "--stage"));
    }

    [Fact]
    public async Task Partial_staging_preserves_the_two_comparisons_and_does_not_write_index()
    {
        using var repo = new TestRepository();
        repo.Write("file.cs", "class Sample\n{\n    int first = 1;\n    int second = 2;\n}\n");
        repo.Commit("initial");
        repo.Write("file.cs", "class Sample\n{\n    int first = 10;\n    int second = 2;\n}\n");
        repo.Git("add", "--", "file.cs");
        repo.Write("file.cs", "class Sample\n{\n    int first = 10;\n    int second = 20;\n}\n");
        var before = repo.Git("ls-files", "--stage");
        var snapshot = await _reader.OpenAsync(repo.Root);
        Assert.Equal(2, snapshot.Changes.Count(c => c.Path == "file.cs"));
        var index = await _reader.DocumentAsync(new(repo.Root, "file.cs", ComparisonKind.Index));
        var work = await _reader.DocumentAsync(new(repo.Root, "file.cs", ComparisonKind.Worktree));
        Assert.Contains("+    int first = 10;", index.Text);
        Assert.DoesNotContain("+    int second = 20;", index.Text);
        Assert.Contains("+    int second = 20;", work.Text);
        Assert.DoesNotContain("+    int first = 10;", work.Text);
        Assert.Equal(repo.Git("show", "HEAD:file.cs"), index.Diff!.Old.Text);
        Assert.Equal(repo.Git("show", ":file.cs"), index.Diff.New.Text);
        Assert.Equal(index.Diff.New.Text, work.Diff!.Old.Text);
        Assert.Equal(2, Assert.Single(index.Diff.Blocks).OldStart);
        Assert.Equal(3, Assert.Single(work.Diff.Blocks).NewStart);
        Assert.Equal(before, repo.Git("ls-files", "--stage"));
    }

    [Fact]
    public async Task History_uses_commit_blobs_not_the_current_worktree_and_handles_root_commit()
    {
        using var repo = new TestRepository();
        repo.Write("历史.txt", "first\n"); repo.Commit("第一个提交");
        var first = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("历史.txt", "second\n"); repo.Commit("第二个提交");
        var second = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("历史.txt", "uncommitted\n");
        var history = await _reader.HistoryAsync(repo.Root, second);
        Assert.Equal(new[] { "第二个提交", "第一个提交" }, history.Commits.Select(c => c.Subject));
        var rootChanges = await _reader.CommitChangesAsync(repo.Root, first);
        Assert.Single(rootChanges);
        var root = await _reader.DocumentAsync(new(repo.Root, "历史.txt", ComparisonKind.Commit, first));
        Assert.Contains("+first", root.Text);
        Assert.Contains("空树", root.Comparison);
        var next = await _reader.DocumentAsync(new(repo.Root, "历史.txt", ComparisonKind.Commit, second));
        Assert.Contains("-first", next.Text);
        Assert.Contains("+second", next.Text);
        Assert.DoesNotContain("uncommitted", next.Text);
    }

    [Fact]
    public async Task Deletion_and_rename_are_read_from_correct_versions()
    {
        using var repo = new TestRepository();
        repo.Write("old name.txt", "rename body\n");
        repo.Write("deleted.txt", "remove body\n"); repo.Commit("initial");
        repo.Git("mv", "old name.txt", "新名字.txt");
        File.Delete(Path.Combine(repo.Root, "deleted.txt"));
        var snapshot = await _reader.OpenAsync(repo.Root);
        var rename = Assert.Single(snapshot.Changes, c => c.Path == "新名字.txt");
        Assert.Equal("old name.txt", rename.OldPath);
        var renamed = await _reader.DocumentAsync(new(repo.Root, rename.Path, rename.Kind, OldPath: rename.OldPath));
        Assert.Equal("old name.txt", renamed.Diff!.Old.Path);
        Assert.Equal("新名字.txt", renamed.Diff.New.Path);
        Assert.Equal("rename body\n", renamed.Diff.Old.Text);
        Assert.Equal(renamed.Diff.Old.Text, renamed.Diff.New.Text);
        Assert.Empty(renamed.Diff.Blocks);
        var removed = await _reader.DocumentAsync(new(repo.Root, "deleted.txt", ComparisonKind.Worktree));
        Assert.Contains("-remove body", removed.Text);
        Assert.DoesNotContain(snapshot.Files, f => f.Path == "deleted.txt");
    }

    [Fact]
    public async Task Literal_special_paths_do_not_expand_to_other_files()
    {
        using var repo = new TestRepository();
        const string special = "目录 空格/[test] 中文.txt";
        repo.Write(special, "old\n"); repo.Write("目录 空格/t 中文.txt", "other\n"); repo.Commit("initial");
        repo.Write(special, "new\n"); repo.Write("目录 空格/t 中文.txt", "must not appear\n");
        var document = await _reader.DocumentAsync(new(repo.Root, special, ComparisonKind.Worktree));
        Assert.Contains("+new", document.Text);
        Assert.DoesNotContain("must not appear", document.Text);
        await Assert.ThrowsAsync<IOException>(() => _reader.DocumentAsync(new(repo.Root, "../outside.txt", ComparisonKind.WorkingFile)));
    }

    [Fact]
    public async Task Empty_crlf_and_no_final_newline_are_preserved()
    {
        using var repo = new TestRepository();
        repo.Write("empty.txt", ""); repo.Write("crlf.txt", "one\r\ntwo\r\n"); repo.Commit("initial");
        var empty = await _reader.DocumentAsync(new(repo.Root, "empty.txt", ComparisonKind.WorkingFile));
        Assert.Equal("", empty.Text);
        var crlf = await _reader.DocumentAsync(new(repo.Root, "crlf.txt", ComparisonKind.WorkingFile));
        Assert.Equal("one\r\ntwo\r\n", crlf.Text);
        Assert.Contains("CRLF", crlf.Detail);
        repo.Write("crlf.txt", "one\r\nchanged");
        var diff = await _reader.DocumentAsync(new(repo.Root, "crlf.txt", ComparisonKind.Worktree));
        Assert.Contains("+changed", diff.Text);
        Assert.Contains("No newline at end of file", diff.Text);
    }

    [Fact]
    public async Task Binary_oversize_and_very_long_line_files_are_bounded()
    {
        using var repo = new TestRepository();
        File.WriteAllBytes(Path.Combine(repo.Root, "binary.bin"), [0, 1, 2, 3]);
        repo.Write("large.txt", new string('x', RepositoryReader.MaxTextBytes + 1));
        repo.Write("long.txt", new string('x', 20001));
        repo.Write("invalid.txt", "");
        File.WriteAllBytes(Path.Combine(repo.Root, "invalid.txt"), [0xff, 0xff, 0xff]);
        var binary = await _reader.DocumentAsync(new(repo.Root, "binary.bin", ComparisonKind.WorkingFile));
        Assert.Contains("二进制", binary.Text);
        var oversized = await _reader.DocumentAsync(new(repo.Root, "large.txt", ComparisonKind.Worktree));
        Assert.Null(oversized.Diff);
        Assert.NotNull(oversized.WriteToken); // Whole-file actions remain available without loading oversized text.
        var line = await _reader.DocumentAsync(new(repo.Root, "long.txt", ComparisonKind.WorkingFile));
        Assert.Contains("20,000", line.Text);
        var invalid = await _reader.DocumentAsync(new(repo.Root, "invalid.txt", ComparisonKind.WorkingFile));
        Assert.Contains("UTF-8", invalid.Text);
        repo.Commit("large fixture");
        repo.Write("invalid.txt", "valid replacement");
        var invalidDiff = await _reader.DocumentAsync(new(repo.Root, "invalid.txt", ComparisonKind.Worktree));
        Assert.Contains("UTF-8", invalidDiff.Text);
        repo.Write("long.txt", new string('y', 20001));
        var longDiff = await _reader.DocumentAsync(new(repo.Root, "long.txt", ComparisonKind.Worktree));
        Assert.Contains("20,000", longDiff.Text);
    }

    [Fact]
    public async Task Linked_worktree_and_subdirectory_resolve_the_actual_root()
    {
        using var repo = new TestRepository();
        repo.Write("nested/a.txt", "content"); repo.Commit("initial");
        var fromSubdirectory = await _reader.OpenAsync(Path.Combine(repo.Root, "nested"));
        Assert.Equal(Path.GetFullPath(repo.Root), fromSubdirectory.Root);
        var linked = Path.Combine(repo.Container, "linked");
        repo.Git("worktree", "add", "-b", "linked-branch", linked);
        var snapshot = await _reader.OpenAsync(linked);
        Assert.Equal("linked-branch", snapshot.Branch);
        Assert.Contains(snapshot.Files, f => f.Path == "nested/a.txt");
        var document = await _reader.DocumentAsync(new(linked, "nested/a.txt", ComparisonKind.WorkingFile));
        Assert.Equal("content", document.Text);
    }

    [Fact]
    public async Task History_is_paged_and_cancellation_is_observed()
    {
        using var repo = new TestRepository();
        repo.Write("a.txt", "a"); repo.Commit("initial");
        for (var i = 0; i < 102; i++) repo.Git("commit", "--allow-empty", "-m", $"commit {i}");
        var head = repo.Git("rev-parse", "HEAD").Trim();
        var first = await _reader.HistoryAsync(repo.Root, head);
        var next = await _reader.HistoryAsync(repo.Root, head, 100);
        Assert.Equal(100, first.Commits.Count); Assert.True(first.HasMore);
        Assert.Equal(3, next.Commits.Count); Assert.False(next.HasMore);
        Assert.Empty(first.Commits.Select(c => c.Sha).Intersect(next.Commits.Select(c => c.Sha)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.OpenAsync(repo.Root, cancellation.Token));
    }

    [Fact]
    public async Task Invalid_directory_has_a_readable_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "GitX-missing-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAnyAsync<Exception>(() => _reader.OpenAsync(path));
    }
}

internal sealed class TestRepository : IDisposable
{
    public string Container { get; } = Path.Combine(Path.GetTempPath(), "GitX.Tests", Guid.NewGuid().ToString("N"));
    public string Root { get; }
    public TestRepository()
    {
        Root = Path.Combine(Container, "repo"); Directory.CreateDirectory(Root);
        Git("init", "-b", "main"); Git("config", "user.name", "GitX Test"); Git("config", "user.email", "gitx-test@example.invalid");
        Git("config", "core.autocrlf", "false"); Git("config", "commit.gpgsign", "false");
    }
    public void Write(string path, string content)
    {
        var full = Path.Combine(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }
    public void Commit(string message) { Git("add", "--all"); Git("commit", "-m", message); }
    public string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(error);
        return output;
    }
    public void Dispose()
    {
        // Exact, randomly created test container; never a user repository.
        if (!Path.GetFullPath(Container).StartsWith(Path.Combine(Path.GetTempPath(), "GitX.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var file in Directory.EnumerateFiles(Container, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        // Reader disposal cancels background reads without blocking the UI.
        // Windows can retain their directory handles briefly after cancellation.
        // Retry sharing/lock violations only; persistent cleanup failures still
        // fail the test, and this never targets anything outside this fixture.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(Container, true); return; }
            catch (IOException e) when (OperatingSystem.IsWindows() && attempt < 20 && (e.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(100); }
        }
    }
}
