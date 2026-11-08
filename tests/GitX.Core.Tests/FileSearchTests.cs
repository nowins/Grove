using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class FileSearchTests
{
    [Theory]
    [InlineData("MVM", "src/MainViewModel.cs")]
    [InlineData("mvm", "src/MainViewModel.cs")]
    [InlineData("src/mvm", "src/MainViewModel.cs")]
    [InlineData("src\\mvm", "src/MainViewModel.cs")]
    [InlineData("./src/mvm", "src/MainViewModel.cs")]
    [InlineData("src model", "src/MainViewModel.cs")]
    [InlineData("主视图", "中文/主窗口视图.cs")]
    [InlineData("😀视", "中文/😀视图.cs")]
    public async Task Abbreviations_path_tokens_and_unicode_find_inventory_files(string query, string expected)
    {
        string[] paths = ["src/MainViewModel.cs", "中文/主窗口视图.cs", "中文/😀视图.cs", "other/Readme.md"];
        var page = await RepositoryReader.SearchAsync("unused", paths, new(query, FilesOnly: true));
        Assert.Contains(page.Matches, match => match.Path == expected);
        Assert.All(page.Matches, match => { Assert.Null(match.Fingerprint); Assert.Equal(0, match.Length); });
    }

    [Fact]
    public async Task Name_quality_precedes_recency_and_recency_only_reorders_existing_candidates()
    {
        string[] paths = ["src/MainViewModel.cs", "test/MainViewModel.cs", "MainViewModel/Other.cs", "MainViewModel.cs.txt"];
        var page = await RepositoryReader.SearchAsync("unused", paths, new("MainViewModel.cs", FilesOnly: true),
            ["missing/MainViewModel.cs", "MainViewModel.cs.txt", "test/MainViewModel.cs"]);
        Assert.Equal("test/MainViewModel.cs", page.Matches[0].Path);
        Assert.Equal("src/MainViewModel.cs", page.Matches[1].Path);
        Assert.Equal("MainViewModel.cs.txt", page.Matches[2].Path);
        Assert.DoesNotContain(page.Matches, match => match.Path.StartsWith("missing/"));
        var noRecent = await RepositoryReader.SearchAsync("unused", paths.Reverse().ToArray(), new("MainViewModel.cs", FilesOnly: true));
        Assert.Equal("src/MainViewModel.cs", noRecent.Matches[0].Path);
    }

    [Fact]
    public async Task Line_and_column_suffixes_are_preserved_and_masks_still_apply()
    {
        var page = await RepositoryReader.SearchAsync("unused", ["src/MainViewModel.cs", "src/MainViewModel.java"],
            new("mvm:42:7", FilesOnly: true, FileMask: "*.cs"));
        var match = Assert.Single(page.Matches);
        Assert.Equal("src/MainViewModel.cs", match.Path); Assert.Equal(42, match.Line); Assert.Equal(7, match.Column);
        Assert.Empty((await RepositoryReader.SearchAsync("unused", ["src/MainViewModel.cs"], new("mvm:0", FilesOnly: true))).Matches);
    }

    [Fact]
    public async Task Top_results_are_independent_of_inventory_order_and_exact_limit_is_not_truncated()
    {
        var paths = Enumerable.Range(0, 20000).Select(i => $"src/{i:D5}/MainViewModel.cs").ToArray();
        var a = await RepositoryReader.SearchAsync("unused", paths, new("MVM", FilesOnly: true));
        var b = await RepositoryReader.SearchAsync("unused", paths.Reverse().ToArray(), new("MVM", FilesOnly: true));
        Assert.True(a.Truncated); Assert.Equal(1000, a.Matches.Count); Assert.Equal(a.Matches, b.Matches);
        Assert.False((await RepositoryReader.SearchAsync("unused", paths[..1000], new("MVM", FilesOnly: true))).Truncated);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FileSearch.Find(paths, new("MVM", FilesOnly: true), null, cancel.Token));
    }

    [Fact]
    public async Task Filename_abbreviations_do_not_change_literal_content_search()
    {
        using var repo = new TestRepository(); repo.Write("MainViewModel.cs", "MainViewModel\nMVM\n");
        var text = await RepositoryReader.SearchAsync(repo.Root, ["MainViewModel.cs"], new("MVM"));
        Assert.Equal(2, Assert.Single(text.Matches).Line);
    }

    [Fact]
    public async Task All_publishes_file_prefix_before_final_results_and_preserves_text_fingerprints()
    {
        using var repo = new TestRepository(); repo.Write("needle.cs", "needle\n");
        var ready = new TaskCompletionSource<CodeSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = RepositoryReader.SearchEverywhereAsync(repo.Root, ["needle.cs"], new("needle"), null, async page =>
        { ready.SetResult(page); await resume.Task; });
        var files = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(Assert.Single(files.Matches).Fingerprint); Assert.False(search.IsCompleted);
        resume.SetResult(); var final = await search;
        Assert.Equal(files.Matches[0], final.Matches[0]); Assert.NotNull(final.Matches[1].Fingerprint);
    }

    [Fact]
    public async Task Cancelled_or_failed_publication_does_not_return_final_results_or_leak_read_slots()
    {
        using var repo = new TestRepository(); repo.Write("needle.cs", "needle\n");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepositoryReader.SearchEverywhereAsync(repo.Root,
            ["needle.cs"], new("needle"), null, _ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryReader.SearchEverywhereAsync(repo.Root,
            ["needle.cs"], new("needle"), null, _ => throw new InvalidOperationException("publisher failed")));
        var retry = await RepositoryReader.SearchEverywhereAsync(repo.Root, ["needle.cs"], new("needle")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, retry.Matches.Count);
    }
}
