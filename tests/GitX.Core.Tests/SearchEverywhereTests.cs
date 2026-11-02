using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class SearchEverywhereTests
{
    [Fact]
    public async Task All_keeps_file_and_text_hits_distinct_and_preserves_content_coordinates()
    {
        using var repo = new TestRepository();
        repo.Write("AGENTS.md", "# AGENTS.md\n"); repo.Write("other.txt", "AGENTS.md\n");
        var page = await RepositoryReader.SearchEverywhereAsync(repo.Root, ["AGENTS.md", "other.txt"], new("AGENTS.md", FileMask: "*.md"));
        Assert.Equal(2, page.Matches.Count);
        Assert.Null(page.Matches[0].Fingerprint); Assert.Equal("AGENTS.md", page.Matches[0].Path);
        Assert.NotNull(page.Matches[1].Fingerprint); Assert.Equal(1, page.Matches[1].Line); Assert.Equal(3, page.Matches[1].Column);
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task Combined_results_share_one_limit_and_report_truncation()
    {
        using var repo = new TestRepository(); repo.Write("text.txt", "needle\n");
        var paths = Enumerable.Range(0, RepositoryReader.MaxSearchResults).Select(index => $"needle-{index}.md").Append("text.txt").ToArray();
        var page = await RepositoryReader.SearchEverywhereAsync(repo.Root, paths, new("needle"));
        Assert.Equal(RepositoryReader.MaxSearchResults, page.Matches.Count); Assert.True(page.Truncated);
        Assert.All(page.Matches, match => Assert.Null(match.Fingerprint));
    }

    [Fact]
    public async Task Cancelled_all_search_does_not_return_a_partial_file_page()
    {
        using var repo = new TestRepository(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepositoryReader.SearchEverywhereAsync(repo.Root, ["AGENTS.md"], new("AGENTS.md"), cancellation.Token));
    }
}
