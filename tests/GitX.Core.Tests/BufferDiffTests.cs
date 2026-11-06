using System.Text.Json;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class BufferDiffTests
{
    [Fact]
    public async Task Buffer_hunks_and_highlights_match_git_for_seeded_edits_and_eof()
    {
        using var fixture = new TestRepository();
        var random = new Random(5704);
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 80)).Select(_ => new[] { "", "  return value;", "🙂 中文", "repeat", "\t x = 1;" }[random.Next(5)]).ToList();
            var original = string.Join('\n', lines) + (iteration % 2 == 0 ? "\n" : "");
            for (var edit = 0; edit < 8; edit++)
            {
                var position = random.Next(lines.Count + 1);
                if (position < lines.Count && random.Next(2) == 0) lines.RemoveAt(position);
                else lines.Insert(position, "新增 " + random.Next(4));
            }
            var modified = string.Join('\n', lines) + (iteration % 3 == 0 ? "\n" : "");
            fixture.Write("old.txt", original); fixture.Write("new.txt", modified);
            var expected = await GitReadProcess.RunAsync(fixture.Root,
                ["-c", "core.autocrlf=false", "diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-color", "--diff-algorithm=myers", "--no-indent-heuristic", "--unified=0", "--", "old.txt", "new.txt"], default, allowDifferences: true);
            Assert.True(BufferDiff.TryCompare(original, modified, default, out var actual));
            var oldSide = new DiffSide("a.txt", "old", original, true);
            var newSide = new DiffSide("a.txt", "new", modified, true);
            Assert.Equal(JsonSerializer.Serialize(NativeDiff.Build(oldSide, newSide, expected)),
                JsonSerializer.Serialize(NativeDiff.Build(oldSide, newSide, actual)));
        }
    }

    [Fact]
    public async Task Concurrent_buffers_are_isolated_and_identical_text_has_no_patch()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            Assert.True(BufferDiff.TryCompare($"old {i}\n", $"new {i}\n", default, out var patch));
            Assert.Contains($"-old {i}\n+new {i}\n", patch);
            Assert.True(BufferDiff.TryCompare($"same {i}", $"same {i}", default, out var identical));
            Assert.Empty(identical);
            return patch;
        })));
        Assert.Equal(16, results.Distinct().Count());
        foreach (var (before, after) in new[] { ("", "text\n"), ("text\n", "") })
        {
            Assert.True(BufferDiff.TryCompare(before, after, default, out var patch));
            Assert.DoesNotContain("new file mode", patch);
            Assert.DoesNotContain("deleted file mode", patch);
            Assert.Contains("e69de29", patch); // Empty content, not a missing file.
        }
    }

    [Fact]
    public async Task Large_or_pathological_inputs_keep_the_interruptible_process_path()
    {
        Assert.False(BufferDiff.TryCompare(new string('a', 256 * 1024 + 1), "", default, out _));
        Assert.False(BufferDiff.TryCompare(new string('\n', 4000), new string('\n', 4000), default, out _));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BufferDiff.TryCompare("a", "b", cancelled.Token, out _));
        var lines = Enumerable.Range(0, 5000).Select(i => $"line {i}").ToArray();
        var before = string.Join('\n', lines) + "\n";
        lines[2500] = "changed line";
        var after = string.Join('\n', lines) + "\n";
        var (_, diff) = await NativeDiff.CompareAsync(new("a", "old", before, true), new("a", "new", after, true), default);
        var block = Assert.Single(diff.Blocks);
        Assert.Equal((2500, 1, 2500, 1), (block.OldStart, block.OldCount, block.NewStart, block.NewCount));
    }
}
