using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class CodeSearchTests
{
    [Fact]
    public async Task Searches_tracked_changes_and_untracked_but_not_ignored_files_with_utf16_coordinates()
    {
        using var repo = new TestRepository(); repo.Write(".gitignore", "ignored.txt\n"); repo.Write("src/a.cs", "old\n"); repo.Commit("base");
        repo.Write("src/a.cs", "// 注释 😀 target\r\nTarget target targetExtra\r\n"); repo.Write("new.txt", "target\n"); repo.Write("ignored.txt", "target\n");
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        var page = await RepositoryReader.SearchAsync(repo.Root, snapshot.Files.Select(file => file.Path).ToArray(), new("target"));
        Assert.Equal(5, page.Matches.Count); Assert.DoesNotContain(page.Matches, match => match.Path == "ignored.txt");
        var first = page.Matches.First(match => match.Path == "src/a.cs"); Assert.Equal(1, first.Line); Assert.Equal(10, first.Column); Assert.Equal(6, first.Length);
        var document = await reader.DocumentAsync(new(repo.Root, first.Path, ComparisonKind.WorkingFile)); Assert.Equal(RepositoryReader.SearchFingerprint(document.Text), first.Fingerprint);
        var exact = await RepositoryReader.SearchAsync(repo.Root, ["src/a.cs"], new("target", MatchCase: true, WholeWord: true)); Assert.Equal(2, exact.Matches.Count);
    }

    [Fact]
    public async Task Regex_masks_unicode_whole_word_and_multiple_matches_keep_original_columns()
    {
        using var repo = new TestRepository(); repo.Write("src/a.java", "测试 test 测试A 测试_test Test test\n"); repo.Write("src/a.cs", "test\n"); repo.Commit("base");
        var page = await RepositoryReader.SearchAsync(repo.Root, ["src/a.java", "src/a.cs"], new("t.st", RegularExpression: true, MatchCase: true, FileMask: "src/*.java;*.md"));
        Assert.Equal(3, page.Matches.Count); Assert.All(page.Matches, match => Assert.Equal("src/a.java", match.Path));
        var word = await RepositoryReader.SearchAsync(repo.Root, ["src/a.java"], new("测试", WholeWord: true)); Assert.Single(word.Matches); Assert.Equal(1, word.Matches[0].Column);
        var zero = await RepositoryReader.SearchAsync(repo.Root, ["src/a.cs"], new("^", RegularExpression: true)); Assert.Equal(2, zero.Matches.Count); Assert.All(zero.Matches, match => Assert.Equal(0, match.Length));
    }

    [Fact]
    public async Task File_search_ranks_names_matches_path_tokens_and_accepts_line_suffix()
    {
        var page = await RepositoryReader.SearchAsync("unused", ["src/sub/alpha.cs", "src/alpha.cs", "alpha.cs.txt", "test/beta.cs"], new("alpha.cs:42", FilesOnly: true));
        Assert.Equal("src/alpha.cs", page.Matches[0].Path); Assert.All(page.Matches, match => Assert.Equal(42, match.Line));
        var tokens = await RepositoryReader.SearchAsync("unused", ["src/sub/alpha.cs", "other/alpha.cs"], new("src alpha", FilesOnly: true)); Assert.Single(tokens.Matches);
    }

    [Fact]
    public async Task Skips_binary_invalid_utf8_large_long_line_missing_and_outside_paths()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "needle\n"); repo.Commit("base");
        File.WriteAllBytes(Path.Combine(repo.Root, "binary"), [0, 1, 2]); File.WriteAllBytes(Path.Combine(repo.Root, "invalid"), [255, 254, 255]);
        repo.Write("large", new string('x', RepositoryReader.MaxTextBytes + 1)); repo.Write("long", new string('x', 20001));
        var page = await RepositoryReader.SearchAsync(repo.Root, ["binary", "invalid", "large", "long", "missing", "../outside", "a.txt"], new("needle"));
        Assert.Single(page.Matches); Assert.Equal(6, page.SkippedFiles); Assert.Equal(1, page.SearchedFiles);
    }

    [Fact]
    public async Task Bounds_results_and_reports_exact_limit_without_false_truncation()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", string.Join('\n', Enumerable.Repeat("needle", 1000))); repo.Commit("base");
        var exact = await RepositoryReader.SearchAsync(repo.Root, ["a.txt"], new("needle")); Assert.Equal(1000, exact.Matches.Count); Assert.False(exact.Truncated);
        repo.Write("a.txt", string.Join('\n', Enumerable.Repeat("needle", 1001)));
        var more = await RepositoryReader.SearchAsync(repo.Root, ["a.txt"], new("needle")); Assert.Equal(1000, more.Matches.Count); Assert.True(more.Truncated);
        var files = await RepositoryReader.SearchAsync("unused", Enumerable.Range(0, 1001).Select(i => $"a{i}.txt").ToArray(), new("a", FilesOnly: true)); Assert.True(files.Truncated);
    }

    [Fact]
    public async Task Empty_invalid_regex_timeout_and_cancellation_are_explicit()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", new string('a', 10000) + "!\n"); repo.Commit("base");
        Assert.Empty((await RepositoryReader.SearchAsync(repo.Root, ["a.txt"], new(""))).Matches);
        await Assert.ThrowsAsync<IOException>(() => RepositoryReader.SearchAsync(repo.Root, ["a.txt"], new("[", RegularExpression: true)));
        await Assert.ThrowsAsync<IOException>(() => RepositoryReader.SearchAsync(repo.Root, ["a.txt"], new("(a+)+$", RegularExpression: true)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepositoryReader.SearchAsync(repo.Root, ["a.txt"], new("a"), cancel.Token));
    }

    [Fact]
    public async Task Opens_filtered_search_result_with_exact_selection_and_rejects_changed_content()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "first\nneedle\n"); repo.Commit("base");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.Filter = "other";
        var page = await RepositoryReader.SearchAsync(repo.Root, model.RepositoryPaths, new("needle")); var match = Assert.Single(page.Matches);
        Assert.True(await model.OpenCodeSearchMatchAsync(model.Root!, match)); Assert.Equal(2, model.CurrentDocument!.RevealLine); Assert.Equal(6, model.CurrentDocument.RevealLength);
        Assert.Equal("other", model.Filter); Assert.Single(model.SourceTabs);
        repo.Write("a.txt", "inserted\nfirst\nneedle\n");
        Assert.False(await model.OpenCodeSearchMatchAsync(model.Root!, match)); Assert.Null(model.CurrentDocument); Assert.Contains("内容已变化", model.Error);
        using var other = new TestRepository(); other.Write("a.txt", "different\n"); other.Commit("other"); await model.OpenAsync(other.Root);
        Assert.False(await model.OpenCodeSearchMatchAsync(repo.Root, match));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_search_navigation_cannot_replace_a_new_file_or_closed_window(bool close)
    {
        var reader = new DelayedReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        var opening = model.OpenCodeSearchMatchAsync("repo", new("a.txt", 1, 1, 3, "old", RepositoryReader.SearchFingerprint("old")));
        var old = reader.Reads[0];
        if (close) model.Dispose();
        else
        {
            model.SelectedEntry = model.Entries.First(entry => entry.Path == "b.txt");
            reader.Reads[1].SetResult(new("b.txt", "current", "plaintext", "working", ""));
            for (var i = 0; i < 100 && model.CurrentDocument is null; i++) await Task.Delay(10);
            Assert.Equal("b.txt", model.CurrentDocument!.Path);
        }
        old.SetResult(new("a.txt", "old", "plaintext", "working", ""));
        Assert.False(await opening); Assert.NotEqual("a.txt", model.CurrentDocument?.Path);
    }

    private sealed class DelayedReader : IRepositoryReader
    {
        public List<TaskCompletionSource<RepositoryDocument>> Reads { get; } = [];
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", null, [new("a.txt"), new("b.txt")], [], false));
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default) { var read = new TaskCompletionSource<RepositoryDocument>(TaskCreationOptions.RunContinuationsAsynchronously); Reads.Add(read); return read.Task; }
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage([], false));
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
    }
}
