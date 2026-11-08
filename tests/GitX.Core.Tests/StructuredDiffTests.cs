using System.Text.Json;
using GitX.Core;
using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class StructuredDiffTests
{
    [Theory]
    [InlineData(DiffWhitespace.Exact)]
    [InlineData(DiffWhitespace.Trim)]
    [InlineData(DiffWhitespace.Ignore)]
    public async Task Structured_computation_defers_patch_and_keeps_legacy_text_and_highlights(DiffWhitespace whitespace)
    {
        var old = new DiffSide("a.py", "old", "  same\r\n\tvalue = '中文🙂';\n\n", true);
        var next = new DiffSide("a.py", "new", "same\nvalue = '新值🙂'; \n ", true);
        var result = await NativeDiff.CompareCapturedAsync(old, next, "captured", default, whitespace);
        Assert.False(result.Patch.IsValueCreated); Assert.True(result.HasPatch);
        var structural = await NativeDiff.CompareStructureAsync(old, next, default, whitespace);
        Assert.Equal(JsonSerializer.Serialize(result.Diff), JsonSerializer.Serialize(structural));
        var texts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => result.Patch.Value)));
        Assert.All(texts, text => Assert.Same(texts[0], text));
        Assert.DoesNotContain('\u0001', texts[0]);
        var expected = await NativeDiff.CompareAsync(old, next, default, whitespace);
        Assert.Equal(expected.Patch, texts[0]);
        Assert.Equal(JsonSerializer.Serialize(NativeDiff.Build(old, next, texts[0], whitespace: whitespace)), JsonSerializer.Serialize(result.Diff));
    }

    [Fact]
    public void Deferred_document_text_obeys_copy_override_and_json_contracts()
    {
        var evaluations = 0;
        var original = new RepositoryDocument("a", "", "text", "compare", "", IsDiff: true,
            Diff: new(new("a", "old", "before", true), new("a", "new", "after", true), [], false))
            { DeferredText = new(() => { Interlocked.Increment(ref evaluations); return "fallback patch 中文\n"; }) };
        var copy = original with { PreserveView = true };
        using var wire = JsonDocument.Parse(EditorServer.Serialize(1, copy));
        Assert.Equal("", wire.RootElement.GetProperty("document").GetProperty("text").GetString());
        Assert.Equal(0, evaluations);
        var replaced = original with { Text = "replacement" };
        Assert.Equal("replacement", replaced.Text); Assert.Null(replaced.DeferredText); Assert.Equal(0, evaluations);
        Assert.Equal("fallback patch 中文\n", copy.Text); Assert.Same(copy.Text, original.Text); Assert.Equal(1, evaluations);
        var json = JsonSerializer.Serialize(copy);
        using var serialized = JsonDocument.Parse(json);
        Assert.Equal(new[] { "Path", "Text", "Language" }, serialized.RootElement.EnumerateObject().Take(3).Select(property => property.Name));
        Assert.DoesNotContain("DeferredText", json);
        Assert.Equal(copy.Text, JsonSerializer.Deserialize<RepositoryDocument>(json)!.Text);
    }

    [Fact]
    public async Task Empty_and_cli_fallback_results_keep_the_same_document_contract()
    {
        var empty = new DiffSide("a", "side", "", true);
        var same = await NativeDiff.CompareCapturedAsync(empty, empty, "empty", default, DiffWhitespace.Exact);
        Assert.False(same.HasPatch); Assert.Empty(same.Diff.Blocks); Assert.Equal("", same.Patch.Value);
        var lines = Enumerable.Range(0, 4100).Select(i => "line " + i).ToArray();
        var before = string.Join('\n', lines) + "\n"; lines[2000] = "changed";
        var old = empty with { Text = before }; var next = empty with { Text = string.Join('\n', lines) + "\n" };
        var result = await NativeDiff.CompareCapturedAsync(old, next, "fallback", default, DiffWhitespace.Trim);
        Assert.False(result.Patch.IsValueCreated); Assert.True(result.HasPatch);
        Assert.Single(result.Diff.Blocks); Assert.Equal(2000, result.Diff.Blocks[0].OldStart);
        Assert.DoesNotContain('\u0001', result.Patch.Value);
        Assert.Equal(JsonSerializer.Serialize(result.Diff), JsonSerializer.Serialize(NativeDiff.Build(old, next, result.Patch.Value, whitespace: DiffWhitespace.Trim)));
    }

    [Fact]
    public async Task Repository_diff_publication_skips_patch_but_fallback_survives_later_file_changes()
    {
        using var fixture = new TestRepository();
        fixture.Write("a.txt", "old\n"); fixture.Commit("base"); fixture.Write("a.txt", "new 中文\n");
        using var reader = new RepositoryReader();
        var document = await reader.DocumentAsync(new(fixture.Root, "a.txt", ComparisonKind.Worktree));
        Assert.NotNull(document.DeferredText); Assert.False(document.DeferredText.IsValueCreated);
        var token = document.WriteToken;
        using var server = new EditorServer(); using var http = new HttpClient();
        server.Publish(server.PreparePublication(1, document, new("a.txt", false, ""), repository: fixture.Root));
        using var response = JsonDocument.Parse(await http.GetByteArrayAsync(new Uri(server.Address, "document")));
        Assert.Equal("", response.RootElement.GetProperty("document").GetProperty("text").GetString());
        Assert.False(document.DeferredText.IsValueCreated);
        fixture.Write("a.txt", "later\n");
        Assert.Contains("+new 中文\n", document.Text); Assert.DoesNotContain("later", document.Text);
        Assert.Equal(token, document.WriteToken); Assert.NotEqual(token, RepositoryState.Token(fixture.Root, ["a.txt"]));
        Assert.Equal("", fixture.Git("diff", "--cached", "--raw"));
    }

    [Fact]
    public void Structured_ranges_reject_negative_overflow_overlap_and_false_context()
    {
        var side = new DiffSide("a", "side", "a\nb\n", true);
        foreach (var hunk in new[] { new DiffHunk(-1, 1, 0, 1), new(0, -1, 0, 0), new(1, int.MaxValue, 1, 1), new(0, 1, 1, 1) })
            Assert.Throws<InvalidDataException>(() => NativeDiff.Build(side, side, new[] { hunk }));
        Assert.Throws<InvalidDataException>(() => NativeDiff.Build(side, side, new[] { new DiffHunk(0, 1, 0, 1), new(0, 1, 0, 1) }));
        Assert.Throws<InvalidDataException>(() => NativeDiff.Build(side, side with { Text = "other\nb\n" }, Array.Empty<DiffHunk>()));
    }
}
