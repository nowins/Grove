using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class CommitRecoveryTests
{
    private static string RecoveryRoot(TestRepository repo) => Path.Combine(repo.Root, ".git/gitx-recovery");
    private static string[] Recoveries(TestRepository repo) => Directory.Exists(RecoveryRoot(repo)) ? Directory.GetDirectories(RecoveryRoot(repo)) : [];
    private static async Task<CommitFileSelection> Selection(TestRepository repo, string path = "a.txt")
    {
        using var reader = new RepositoryReader(); var request = new DocumentRequest(repo.Root, path, ComparisonKind.LocalChanges);
        return new(request, await reader.DocumentAsync(request), null);
    }
    private static void Hook(TestRepository repo, string script)
    {
        var path = Path.Combine(repo.Root, ".git/hooks/pre-commit");
        File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + script + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Twenty_successful_commits_do_not_accumulate_private_index_copies()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base");
        for (var i = 1; i <= 20; i++)
        {
            var text = $"selected {i}"; repo.Write("a.txt", text);
            var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo)], $"selected {i}");
            Assert.Null(result.Warning); Assert.Equal(result.Sha, repo.Git("rev-parse", "HEAD").Trim());
            Assert.Equal(text, repo.Git("show", ":a.txt")); Assert.Equal(text, File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
            Assert.Empty(Recoveries(repo)); Assert.Empty(Directory.GetFiles(RecoveryRoot(repo), "*", SearchOption.AllDirectories));
        }
        Assert.Equal("21", repo.Git("rev-list", "--count", "HEAD").Trim()); Assert.Empty(repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task First_commit_without_an_original_index_cleans_its_private_indexes()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "first");
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git/index")));
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo)], "first");
        Assert.Null(result.Warning); Assert.Equal("first", repo.Git("show", "HEAD:a.txt")); Assert.Empty(Recoveries(repo));
        Assert.Empty(repo.Git("diff", "--cached"));
    }

    [Fact]
    public async Task Reconciled_hook_commit_keeps_discard_and_legacy_recoveries_and_unselected_staging()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Write("discard.txt", "base"); repo.Write("keep.txt", "base"); repo.Commit("base");
        repo.Write("discard.txt", "recover this content");
        using var reader = new RepositoryReader(); var request = new DocumentRequest(repo.Root, "discard.txt", ComparisonKind.Worktree);
        var document = await reader.DocumentAsync(request);
        var recovery = await new RepositoryWriter().FileAsync(request, document.WriteToken!, FileWriteOperation.Discard);
        Assert.NotNull(recovery);
        var oldFiles = Directory.GetFiles(recovery.Directory).ToDictionary(path => path, File.ReadAllBytes);
        var legacy = Path.Combine(RecoveryRoot(repo), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "original.index"), "unclassified old index; preserve");
        repo.Write("keep.txt", "unselected staged"); repo.Git("add", "keep.txt");
        repo.Write("a.txt", "selected"); repo.Write("hook.txt", "hook content"); Hook(repo, "git add hook.txt");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo)], "hook reconciled");
        Assert.Null(result.Warning); Assert.Equal("hook content", repo.Git("show", "HEAD:hook.txt")); Assert.Equal("hook content", repo.Git("show", ":hook.txt"));
        Assert.Equal("keep.txt", repo.Git("diff", "--cached", "--name-only").Trim());
        Assert.Equal(new[] { recovery.Directory, legacy }.Select(Path.GetFullPath).Order(), Recoveries(repo).Select(Path.GetFullPath).Order());
        foreach (var (path, bytes) in oldFiles) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal("unclassified old index; preserve", File.ReadAllText(Path.Combine(legacy, "original.index")));
    }

    [Fact]
    public async Task Failed_hook_material_is_not_removed_by_a_later_successful_commit()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "selected");
        var head = repo.Git("rev-parse", "HEAD"); var index = repo.Git("write-tree"); Hook(repo, "exit 1");
        var selection = await Selection(repo);
        await Assert.ThrowsAsync<IOException>(() => new CommitSelectionWriter().CommitAsync(repo.Root, [selection], "fails"));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("write-tree"));
        var failed = Assert.Single(Recoveries(repo)); var material = Directory.GetFiles(failed).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Contains(Path.Combine(failed, "original.index"), material.Keys); Assert.Contains(Path.Combine(failed, "commit.index"), material.Keys);
        Hook(repo, "true");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo)], "succeeds");
        Assert.Null(result.Warning); Assert.Equal(failed, Assert.Single(Recoveries(repo)));
        foreach (var (path, bytes) in material) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_hook_files_or_subdirectories_are_preserved_with_an_accurate_success_notice(bool directory)
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "selected");
        Hook(repo, directory ? "mkdir \"$GIT_INDEX_FILE.extra\"\nprintf 'preserve' > \"$GIT_INDEX_FILE.extra/note\"" : "printf 'preserve' > \"$GIT_INDEX_FILE.extra\"");
        var result = await new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo)], "keep unknown material");
        Assert.Null(result.Warning); Assert.NotNull(result.Notice);
        Assert.Contains("索引已同步", result.Notice); Assert.Contains("未清理", result.Notice); Assert.Contains(result.Notice, result.Output);
        Assert.Equal(result.Sha, repo.Git("rev-parse", "HEAD").Trim()); Assert.Empty(repo.Git("diff", "--cached"));
        var retained = Assert.Single(Recoveries(repo));
        Assert.True(File.Exists(Path.Combine(retained, "original.index"))); Assert.True(File.Exists(Path.Combine(retained, "remaining.index")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(retained, directory ? "commit.index.extra/note" : "commit.index.extra")));
    }

    [Fact]
    public async Task A_locked_temporary_index_cannot_turn_a_completed_commit_into_a_failed_commit()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "selected");
        var signal = Path.Combine(repo.Root, ".git/private-index-path"); var release = Path.Combine(repo.Root, ".git/release-hook");
        Hook(repo, "printf '%s' \"$GIT_INDEX_FILE\" > .git/private-index-path\nwhile [ ! -f .git/release-hook ]; do sleep 0.05; done");
        var operation = new CommitSelectionWriter().CommitAsync(repo.Root, [await Selection(repo)], "cleanup lock");
        try
        {
            for (var i = 0; !File.Exists(signal) && !operation.IsCompleted && i < 400; i++) await Task.Delay(25);
            Assert.True(File.Exists(signal));
            var directory = Path.GetDirectoryName(File.ReadAllText(signal))!;
            Assert.Equal(Path.GetFullPath(RecoveryRoot(repo)), Path.GetDirectoryName(Path.GetFullPath(directory)));
            using var held = new FileStream(Path.Combine(directory, "original.index"), FileMode.Open, FileAccess.Read, FileShare.Read);
            File.WriteAllText(release, "continue"); var result = await operation;
            Assert.Equal(result.Sha, repo.Git("rev-parse", "HEAD").Trim()); Assert.Equal("selected", repo.Git("show", ":a.txt"));
            Assert.Empty(repo.Git("diff", "--cached"));
            if (OperatingSystem.IsWindows())
            {
                Assert.Null(result.Warning); Assert.Contains("索引已同步", result.Notice); Assert.Contains("未清理", result.Notice);
                Assert.True(File.Exists(Path.Combine(directory, "original.index")));
            }
            else { Assert.Null(result.Warning); Assert.Null(result.Notice); Assert.Empty(Recoveries(repo)); } // Unix permits unlinking an open file.
        }
        finally { File.WriteAllText(release, "continue"); await operation; }
    }

    [Fact]
    public async Task Cleanup_notice_in_the_commit_view_reports_success_and_clears_the_committed_selection()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base"); repo.Write("a.txt", "selected");
        Hook(repo, "printf 'preserve' > \"$GIT_INDEX_FILE.extra\"");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(1);
        model.ToggleIncludeFile("a.txt"); model.CommitMessage = "one commit only"; await model.CommitAsync();
        Assert.False(model.HasError); Assert.Contains("提交已完成", model.Status); Assert.Contains("索引已同步", model.Status); Assert.Contains("未清理", model.Status);
        Assert.Equal("已完成", model.LastGitOperation!.Snapshot().Phase);
        Assert.Contains("未清理", model.LastOperationOutput); Assert.Contains("未清理", model.LastGitOperation.Snapshot().Output);
        Assert.Equal("", model.CommitMessage); Assert.Equal(0, model.IncludedFileCount);
        Assert.Equal("2", repo.Git("rev-list", "--count", "HEAD").Trim()); Assert.Empty(repo.Git("status", "--porcelain"));
    }
}
