using System.Text.Json;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class SharedDiffTests
{
    [Fact]
    public async Task Identical_diffs_keep_each_repositorys_context_and_recheck_changed_contents()
    {
        using var a = new TestRepository(); using var b = new TestRepository();
        foreach (var repo in new[] { a, b })
        { repo.Write("text.txt", "alpha old\n"); repo.Commit("base"); repo.Write("text.txt", "alpha new\n"); }
        using var reader = new RepositoryReader();
        var documents = await Task.WhenAll(new[] { a, b }.Select(repo => reader.DocumentAsync(new(repo.Root, "text.txt", ComparisonKind.Worktree))));
        Assert.Equal(JsonSerializer.Serialize(documents[0].Diff), JsonSerializer.Serialize(documents[1].Diff));
        Assert.NotEqual(documents[0].CommentContext!.RepositoryId, documents[1].CommentContext!.RepositoryId);
        for (var i = 0; i < 2; i++)
        {
            var repo = i == 0 ? a : b;
            Assert.Equal(repo.Root, documents[i].CommentContext!.Root);
            Assert.Equal(RepositoryState.Token(repo.Root, ["text.txt"]), documents[i].WriteToken);
            Assert.True(documents[i].CanStage);
        }
        a.Write("text.txt", "alpha newer\n");
        var changed = await reader.DocumentAsync(new(a.Root, "text.txt", ComparisonKind.Worktree));
        Assert.Equal("alpha newer\n", changed.Diff!.New.Text);
        Assert.NotEqual(documents[0].WriteToken, changed.WriteToken);
        Assert.NotEqual(documents[0].CommentContext!.Snapshot, changed.CommentContext!.Snapshot);
        Assert.Equal("alpha new\n", File.ReadAllText(Path.Combine(b.Root, "text.txt")));
        Assert.Equal("", b.Git("diff", "--cached", "--raw"));
    }

    [Fact]
    public void Identity_covers_exact_text_path_label_existence_and_whitespace()
    {
        var old = new DiffSide("old.txt", "before", "old\r\n🙂", true);
        var next = new DiffSide("new.txt", "after", "new\n中文", true);
        var key = new DiffInput(old, next, DiffWhitespace.Exact);
        var equal = new DiffInput(old with { Text = new string(old.Text.ToCharArray()) }, next with { }, DiffWhitespace.Exact);
        Assert.Equal(key, equal); Assert.Equal(key.GetHashCode(), equal.GetHashCode());
        foreach (var changed in new[] { next with { Path = "other.txt" }, next with { Label = "other revision" },
            next with { Text = next.Text + "\n" }, next with { Exists = false } })
        {
            Assert.NotEqual(key, new DiffInput(old, changed, DiffWhitespace.Exact));
            Assert.NotEqual(new DiffInput(next, old, DiffWhitespace.Exact), new DiffInput(changed, old, DiffWhitespace.Exact));
        }
        Assert.NotEqual(key, new DiffInput(old, next, DiffWhitespace.Trim));
        Assert.NotEqual(key, new DiffInput(old, next, DiffWhitespace.Ignore));
    }

    [Fact]
    public void Captured_identity_is_a_bucket_hint_and_never_substitutes_for_input_equality()
    {
        var old = new DiffSide("a", "before", "old", true); var next = new DiffSide("a", "after", "new", true);
        var key = new DiffInput(old, next, DiffWhitespace.Exact, "same digest");
        var collision = new DiffInput(old, next with { Text = "different" }, DiffWhitespace.Exact, "same digest");
        Assert.Equal(key.GetHashCode(), collision.GetHashCode()); Assert.NotEqual(key, collision);
        Assert.Equal(key, new DiffInput(old with { }, next with { }, DiffWhitespace.Exact, "same digest"));
        Assert.NotEqual(key, new DiffInput(old, next, DiffWhitespace.Trim, "same digest"));
        Assert.NotEqual(key, new DiffInput(old, next, DiffWhitespace.Exact, "other digest"));
    }

    [Theory]
    [InlineData("alpha old\n", "alpha new\n")]
    [InlineData("", "added\n")]
    [InlineData("deleted\n", "")]
    public async Task Every_shared_diff_collection_is_read_only_and_serializes_without_shape_changes(string before, string after)
    {
        var old = new DiffSide("a", "old", before, true); var next = new DiffSide("a", "new", after, true);
        var (patch, diff) = await NativeDiff.CompareAsync(old, next, default);
        var expected = NativeDiff.Build(old, next, patch);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(diff));
        RejectMutation(diff.Blocks);
        foreach (var block in diff.Blocks)
        {
            RejectMutation(block.Rows); RejectMutation(block.InlineChanges);
            foreach (var span in block.InlineChanges)
            {
                RejectMutation(span.OldSpans); RejectMutation(span.NewSpans);
                RejectMutation(span.OldWordSpans!); RejectMutation(span.NewWordSpans!);
            }
        }
    }

    private static void RejectMutation<T>(IReadOnlyList<T> values)
    {
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(default!));
        if (list.Count > 0) Assert.Throws<NotSupportedException>(() => list[0] = default!);
    }
}
