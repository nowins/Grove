using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class IncrementalBlameTests
{
    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    public async Task Incremental_matches_porcelain_for_renames_repeated_metadata_and_local_edits(string newline, bool finalNewline)
    {
        using var repo = new TestRepository(); repo.Git("config", "user.name", "作者 Unicode");
        var lines = Enumerable.Range(1, 90).Select(i => $"line {i:D3} 内容").ToList();
        string Text() => string.Join(newline, lines) + (finalNewline ? newline : "");
        repo.Write("旧 名.txt", Text()); repo.Commit("最初");
        repo.Git("mv", "旧 名.txt", "新 名.txt"); lines[20] = "replace 21"; lines.Insert(4, "inserted"); repo.Write("新 名.txt", Text()); repo.Commit("rename and insert");
        lines.RemoveAt(60); lines[^1] = "committed tail"; repo.Write("新 名.txt", Text()); repo.Commit("delete and tail");
        var revision = repo.Git("rev-parse", "HEAD").Trim();
        Compare(repo, "新 名.txt", lines.Count);
        lines[12] = "local working row"; lines.Insert(50, "local inserted"); repo.Write("新 名.txt", Text());
        Compare(repo, "新 名.txt", lines.Count);
        var document = await new BlameReader().ReadAsync(repo.Root, "新 名.txt");
        Assert.Equal(WithoutNumbers(BlameReader.Parse(repo.Git("blame", "--porcelain", "--", "新 名.txt"))), WithoutNumbers(document.Blame!));
        var historical = await new BlameReader().ReadRevisionAsync(repo.Root, new(revision, "新 名.txt"));
        Assert.Equal(WithoutNumbers(BlameReader.Parse(repo.Git("blame", "--porcelain", revision, "--", "新 名.txt"))), WithoutNumbers(historical.Blame!));
    }

    [Fact]
    public async Task Fully_uncommitted_and_empty_results_need_no_commit_numbers_and_remain_cancellable()
    {
        using var repo = new TestRepository(); repo.Write("a", "old\n"); repo.Write("empty", ""); repo.Commit("base");
        repo.Write("a", string.Join('\n', Enumerable.Range(1, 5000).Select(i => $"brand new {i}")));
        Compare(repo, "a", 5000); var reader = new BlameReader(); var document = await reader.ReadAsync(repo.Root, "a");
        Assert.Equal(5000, document.Blame!.Count); Assert.All(document.Blame, line => { Assert.False(line.IsCommitted); Assert.Null(line.CommitNumber); Assert.True(line.HasPrevious); });
        Assert.Empty((await reader.ReadAsync(repo.Root, "empty")).Blame!);
        using var cancellation = new CancellationTokenSource(); var pending = reader.ReadAsync(repo.Root, "a", cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(document.Blame, (await reader.ReadAsync(repo.Root, "a")).Blame);
    }

    [Fact]
    public void Out_of_order_groups_expand_into_source_order_with_per_commit_metadata_reuse()
    {
        var a = new string('a', 40); var b = new string('b', 40);
        var output = $"{b} 8 4 2\nauthor B\nauthor-time 0\nsummary last\nprevious {a} old.txt\nfilename new.txt\n"
            + $"{a} 1 1 2\nauthor A\nauthor-time 0\nsummary first\nboundary\nfilename old.txt\n"
            + $"{b} 7 3 1\nunknown future metadata\nfilename new.txt\n";
        var result = BlameReader.ParseIncremental(output, 5);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, result.Select(line => line.Line));
        Assert.Equal(new[] { 1, 2, 7, 8, 9 }, result.Select(line => line.OriginalLine));
        Assert.Equal(new[] { "A", "A", "B", "B", "B" }, result.Select(line => line.Author));
        Assert.Equal("last", result[2].Summary); Assert.Equal("old.txt", result[2].PreviousPath);
        Assert.All(result.Skip(2), line => Assert.Equal(a, line.PreviousSha));
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1 1 0\nfilename a\n", 1)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1 2 1\nfilename a\n", 1)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 2147483647 1 2\nfilename a\n", 2)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1 1 1\n", 1)]
    [InlineData("filename a\n", 1)]
    [InlineData("", 1)]
    [InlineData("", 5001)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1 1 1\nfilename a\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 2 1 1\nfilename a\n", 2)]
    public void Invalid_or_partial_ranges_are_rejected(string output, int count)
        => Assert.Throws<InvalidDataException>(() => BlameReader.ParseIncremental(output, count));

    private static void Compare(TestRepository repo, string path, int count)
        => Assert.Equal(BlameReader.Parse(repo.Git("blame", "--porcelain", "--", path)), BlameReader.ParseIncremental(repo.Git("blame", "--incremental", "--", path), count));
    private static IEnumerable<BlameLine> WithoutNumbers(IEnumerable<BlameLine> lines) => lines.Select(line => line with { CommitNumber = null });
}
