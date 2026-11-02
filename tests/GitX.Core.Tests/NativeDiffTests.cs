using System.Globalization;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class NativeDiffTests
{
    [Fact]
    public async Task Inserted_guard_aligns_before_the_modified_return_line()
    {
        var (_, diff) = await Compare("    return amount > 0;\n", "    if (amount > 1000) return false;\n    return amount >= 0.01m;\n");
        var block = Assert.Single(diff.Blocks);
        Assert.Equal(new DiffRow(null, 1), block.Rows[0]);
        Assert.Equal(new DiffRow(1, 2), block.Rows[1]);
        var inline = Assert.Single(block.InlineChanges);
        Assert.Equal(1, inline.OldLine); Assert.Equal(2, inline.NewLine);
    }

    [Fact]
    public async Task Git_blocks_map_leading_insertion_deletion_and_unequal_replacement()
    {
        var oldText = "start\nkeep1\nkeep2\nremove\nkeep3\noldValue = 1;\nkeep4\nend\n";
        var newText = "inserted\nstart\nkeep1\nkeep2\nkeep3\nnewValue = 20;\nextra\nkeep4\nend\n";
        var (_, diff) = await Compare(oldText, newText);
        Assert.Collection(diff.Blocks,
            b => Assert.Equal((0, 0, 0, 1), (b.OldStart, b.OldCount, b.NewStart, b.NewCount)),
            b => Assert.Equal((3, 1, 4, 0), (b.OldStart, b.OldCount, b.NewStart, b.NewCount)),
            b => Assert.Equal((5, 1, 5, 2), (b.OldStart, b.OldCount, b.NewStart, b.NewCount)));
        var inline = Assert.Single(diff.Blocks[2].InlineChanges);
        Assert.Equal(6, inline.OldLine); Assert.Equal(6, inline.NewLine);
        Assert.Equal(new[] { "old", "1" }, Extract("oldValue = 1;", inline.OldSpans));
        Assert.Equal(new[] { "new", "20" }, Extract("newValue = 20;", inline.NewSpans));
    }

    [Theory]
    [InlineData("", "first\nsecond\n", 0, 2)]
    [InlineData("first\nsecond\n", "", 2, 0)]
    [InlineData("\n", "", 1, 0)]
    [InlineData("", "\n", 0, 1)]
    public async Task Empty_side_has_no_fake_source_line(string oldText, string newText, int oldCount, int newCount)
    {
        var (_, diff) = await Compare(oldText, newText);
        var block = Assert.Single(diff.Blocks);
        Assert.Equal((0, oldCount, 0, newCount), (block.OldStart, block.OldCount, block.NewStart, block.NewCount));
        Assert.Equal(oldCount, NativeDiff.Lines(oldText).Length);
        Assert.Equal(newCount, NativeDiff.Lines(newText).Length);
    }

    [Fact]
    public async Task Eof_newline_and_eol_changes_remain_visible_without_fake_char_spans()
    {
        var (_, diff) = await Compare("value\r\n", "value");
        var block = Assert.Single(diff.Blocks);
        Assert.Equal("CRLF", diff.Old.LineEnding); Assert.True(diff.Old.EndsWithNewline);
        Assert.False(diff.New.EndsWithNewline);
        Assert.Equal(1, block.OldCount); Assert.Equal(1, block.NewCount);
        var inline = Assert.Single(block.InlineChanges);
        Assert.Empty(inline.OldSpans); Assert.Empty(inline.NewSpans);
    }

    [Fact]
    public async Task Inline_spans_preserve_emoji_combining_sequences_and_separate_edits()
    {
        const string oldText = "名称 = \"🙂 e\u0301\"; int x = 1;";
        const string newText = "名称 = \"🙃 a\u0301\"; int x = 2;";
        var (_, diff) = await Compare(oldText, newText);
        var inline = Assert.Single(Assert.Single(diff.Blocks).InlineChanges);
        Assert.Equal(new[] { "🙂", "e\u0301", "1" }, Extract(oldText, inline.OldSpans));
        Assert.Equal(new[] { "🙃", "a\u0301", "2" }, Extract(newText, inline.NewSpans));
        foreach (var (text, spans) in new[] { (oldText, inline.OldSpans), (newText, inline.NewSpans) })
        {
            var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
            Assert.All(spans, span => { Assert.Contains(span.StartColumn - 1, boundaries); Assert.Contains(span.EndColumn - 1, boundaries); });
        }
    }

    [Fact]
    public async Task Long_changed_line_uses_bounded_range_highlighting()
    {
        var (_, diff) = await Compare(new string('x', 1000), new string('y', 1000));
        Assert.True(diff.CoarseInlineHighlights);
        var inline = Assert.Single(Assert.Single(diff.Blocks).InlineChanges);
        Assert.Equal(new InlineSpan(1, 1001), Assert.Single(inline.OldSpans));
        Assert.Equal(new InlineSpan(1, 1001), Assert.Single(inline.NewSpans));
    }

    [Fact]
    public async Task Identical_text_has_no_change_and_snapshot_is_immutable()
    {
        using var repo = new TestRepository();
        repo.Write("file.txt", "base\n"); repo.Commit("base");
        repo.Write("file.txt", "first\n");
        var document = await new RepositoryReader().DocumentAsync(new(repo.Root, "file.txt", ComparisonKind.Worktree));
        repo.Write("file.txt", "second\n");
        Assert.Equal("first\n", document.Diff!.New.Text);
        Assert.Equal("base\n", document.Diff.Old.Text);
        Assert.Single(document.Diff.Blocks);
        var (_, identical) = await Compare("same\n", "same\n");
        Assert.Empty(identical.Blocks);
    }

    [Fact]
    public async Task Checkout_eol_filter_avoids_spurious_entire_file_changes()
    {
        using var repo = new TestRepository();
        repo.Write("file.txt", "first\nsecond\nthird\n"); repo.Commit("base");
        repo.Git("config", "core.autocrlf", "true");
        repo.Write("file.txt", "first\r\nupdated\r\nthird\r\n");
        var document = await new RepositoryReader().DocumentAsync(new(repo.Root, "file.txt", ComparisonKind.Worktree));
        var block = Assert.Single(document.Diff!.Blocks);
        Assert.Equal((1, 1, 1, 1), (block.OldStart, block.OldCount, block.NewStart, block.NewCount));
        Assert.Equal("CRLF", document.Diff.Old.LineEnding);
        Assert.Contains("-second", repo.Git("diff", "--", "file.txt"));
    }

    [Fact]
    public async Task Different_eol_without_checkout_filter_only_marks_code_and_keeps_source_bytes()
    {
        using var repo = new TestRepository();
        repo.Git("config", "core.autocrlf", "false");
        repo.Write("Channel.java", "class Channel {\n    int first = 1;\n    int second = 2;\n}\n"); repo.Commit("base");
        const string working = "class Channel {\r\n    int first = 10;\r\n    int second = 2;\r\n}\r\n";
        repo.Write("Channel.java", working);
        var document = await new RepositoryReader().DocumentAsync(new(repo.Root, "Channel.java", ComparisonKind.Worktree));
        var diff = document.Diff!;
        var block = Assert.Single(diff.Blocks);
        Assert.Equal((1, 1, 1, 1), (block.OldStart, block.OldCount, block.NewStart, block.NewCount));
        Assert.Equal("LF", diff.Old.LineEnding); Assert.Equal("CRLF", diff.New.LineEnding);
        Assert.Equal(working, diff.New.Text); Assert.Equal("java", document.Language);
        var (_, separatorsOnly) = await Compare("same\nnext\n", "same\r\nnext\r\n");
        Assert.Empty(separatorsOnly.Blocks);
        var (_, spaces) = await Compare("same \n", "same\r\n");
        Assert.Single(spaces.Blocks); // No implicit trim-whitespace policy.
    }

    [Fact]
    public void Inconsistent_ranges_are_rejected_instead_of_mislabeling_source_lines()
    {
        Assert.Throws<InvalidDataException>(() => NativeDiff.Build(Side("a\n"), Side("b\n"), "@@ -2 +2 @@\n-a\n+b\n"));
        Assert.Throws<InvalidDataException>(() => NativeDiff.Build(Side("a\n"), Side("b\n"), ""));
    }

    [Fact]
    public async Task Whitespace_modes_change_comparison_without_changing_source_or_inline_columns()
    {
        var old = Side("  int value = 1;  \n\treturn value;\n");
        var current = Side("int value = 2;\nreturn value; \n");
        var (_, trimmed) = await NativeDiff.CompareAsync(old, current, default, DiffWhitespace.Trim);
        var block = Assert.Single(trimmed.Blocks);
        Assert.Equal((0, 1, 0, 1), (block.OldStart, block.OldCount, block.NewStart, block.NewCount));
        Assert.Equal(old.Text, trimmed.Old.Text); Assert.Equal(current.Text, trimmed.New.Text);
        var inline = Assert.Single(block.InlineChanges);
        Assert.Equal(new[] { "1" }, Extract("  int value = 1;  ", inline.OldSpans));
        Assert.Equal(new[] { "2" }, Extract("int value = 2;", inline.NewSpans));
        var (_, ignored) = await NativeDiff.CompareAsync(Side("a + b\n"), Side("a+b\n"), default, DiffWhitespace.Ignore);
        Assert.Empty(ignored.Blocks);
        var (_, exact) = await NativeDiff.CompareAsync(Side("a + b\n"), Side("a+b\n"), default);
        Assert.Single(exact.Blocks);
    }

    [Fact]
    public async Task Word_highlighting_marks_both_exception_identifiers_as_whole_tokens()
    {
        var (_, diff) = await Compare("catch (IOException e)\n", "catch (Exception e)\n");
        var inline = Assert.Single(Assert.Single(diff.Blocks).InlineChanges);
        Assert.Equal(new[] { "IOException" }, Extract("catch (IOException e)", inline.OldWordSpans!));
        Assert.Equal(new[] { "Exception" }, Extract("catch (Exception e)", inline.NewWordSpans!));
    }

    [Theory]
    [InlineData(DiffWhitespace.Trim)]
    [InlineData(DiffWhitespace.Ignore)]
    public async Task Ignored_whitespace_at_eof_retains_source_lines_and_missing_newline(DiffWhitespace whitespace)
    {
        (string Old, string New, int Changes)[] cases = [("a\n\n", "a\n ", 1), ("a\n ", "a\n\t", 0),
            ("", " ", 1), (" ", "\n", 1), ("a\n", "a\n ", 1)];
        foreach (var (oldText, newText, changes) in cases)
        {
            var (patch, diff) = await NativeDiff.CompareAsync(Side(oldText), Side(newText), default, whitespace);
            Assert.Equal(changes, diff.Blocks.Count);
            Assert.Equal(oldText, diff.Old.Text); Assert.Equal(newText, diff.New.Text);
            Assert.DoesNotContain('\u0001', patch);
        }
    }

    private static DiffSide Side(string text) => new("test.cs", "test", text, true);
    private static Task<(string Patch, SideBySideDiff Diff)> Compare(string oldText, string newText) => NativeDiff.CompareAsync(Side(oldText), Side(newText), CancellationToken.None);
    private static string[] Extract(string text, IReadOnlyList<InlineSpan> spans) => spans.Select(s => text[(s.StartColumn - 1)..(s.EndColumn - 1)]).ToArray();
}
