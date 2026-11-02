using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class MergePreparationTests
{
    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    public async Task Many_independent_blocks_keep_source_order_ranges_and_git_decisions(string newline, bool finalNewline)
    {
        using var repo = Fixture(newline, finalNewline);
        var head = repo.Git("rev-parse", "HEAD"); var index = repo.Git("ls-files", "--stage");
        var working = File.ReadAllBytes(Path.Combine(repo.Root, "a.txt"));
        var document = await new ConflictReader().ReadAsync(repo.Root, "a.txt");
        var session = await ConflictReader.PrepareSessionAsync(document);
        Assert.Equal(document.Base.Text, session.Result); Assert.Equal(24, session.Blocks.Count(b => b.IsConflict));
        Assert.Contains(session.Blocks, b => b.Length == 0); Assert.Contains(session.Blocks, b => b.LeftCount == 0);
        Assert.Contains(session.Blocks, b => !b.IsConflict && b.LeftChanged); Assert.Contains(session.Blocks, b => !b.IsConflict && b.RightChanged);
        Assert.Equal(Enumerable.Range(0, session.Blocks.Count), session.Blocks.Select(b => b.Id));
        Assert.Equal(session.Blocks.OrderBy(b => b.StartOffset), session.Blocks);
        var leftLines = NativeDiff.Lines(document.Left.Text!); var rightLines = NativeDiff.Lines(document.Right.Text!);
        foreach (var block in session.Blocks)
        {
            Assert.Equal(block.BaseText, document.Base.Text!.Substring(block.StartOffset, block.Length));
            Assert.Equal(NativeDiff.Lines(block.LeftText), leftLines.Skip(block.LeftStart).Take(block.LeftCount));
            Assert.Equal(NativeDiff.Lines(block.RightText), rightLines.Skip(block.RightStart).Take(block.RightCount));
            var expected = await ConflictReader.MergeAsync(block.BaseText, block.LeftText, block.RightText);
            Assert.Equal(expected.Text, block.AutomaticText); Assert.Equal(expected.Blocks.Count > 0, block.IsConflict);
        }
        var again = await ConflictReader.PrepareSessionAsync(document);
        Assert.Equal(session.Blocks, again.Blocks);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("ls-files", "--stage"));
        Assert.Equal(working, File.ReadAllBytes(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task Cancelled_preparation_can_be_awaited_and_retried_without_modifying_the_repository()
    {
        using var repo = Fixture("\n", true);
        var document = await new ConflictReader().ReadAsync(repo.Root, "a.txt");
        var head = repo.Git("rev-parse", "HEAD"); var index = repo.Git("ls-files", "--stage");
        var working = File.ReadAllBytes(Path.Combine(repo.Root, "a.txt"));
        using var cancellation = new CancellationTokenSource();
        var pending = ConflictReader.PrepareSessionAsync(document, cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var session = await ConflictReader.PrepareSessionAsync(document);
        Assert.Equal(24, session.Blocks.Count(b => b.IsConflict));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("ls-files", "--stage"));
        Assert.Equal(working, File.ReadAllBytes(Path.Combine(repo.Root, "a.txt")));
    }

    [Fact]
    public async Task External_changes_still_reject_the_prepared_session()
    {
        using var repo = Fixture("\n", true);
        var document = await new ConflictReader().ReadAsync(repo.Root, "a.txt");
        var index = repo.Git("ls-files", "--stage"); repo.Write("a.txt", "external content\n");
        await Assert.ThrowsAsync<IOException>(() => ConflictReader.PrepareSessionAsync(document));
        Assert.Equal("external content\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal(index, repo.Git("ls-files", "--stage"));
    }

    private static TestRepository Fixture(string newline, bool finalNewline)
    {
        var repo = new TestRepository(); var lines = Enumerable.Range(1, 300).Select(i => $"line {i:D3} 中文").ToArray();
        string Text(IEnumerable<string> source) => string.Join(newline, source) + (finalNewline ? newline : "");
        repo.Write("a.txt", Text(lines)); repo.Commit("base"); repo.Git("switch", "-c", "topic");
        var right = lines.ToArray(); var left = lines.ToArray();
        for (var i = 0; i < 24; i++) { left[10 + 10 * i] = $"left {i}"; right[10 + 10 * i] = $"right {i}"; }
        right[280] = "right clean"; repo.Write("a.txt", Text(right)); repo.Commit("right"); repo.Git("switch", "main");
        left[270] = "left clean"; var leftList = left.ToList(); leftList.RemoveAt(260); leftList.Insert(3, "left insertion");
        repo.Write("a.txt", Text(leftList)); repo.Commit("left"); Assert.Throws<IOException>(() => repo.Git("merge", "topic"));
        return repo;
    }
}
