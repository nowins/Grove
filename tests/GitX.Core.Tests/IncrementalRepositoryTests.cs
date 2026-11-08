using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class IncrementalRepositoryTests
{
    [Theory]
    [InlineData("a.txt")]
    [InlineData("目录/[literal].txt")]
    [InlineData("build/tracked.txt")]
    public async Task Content_refresh_matches_full_scan_and_preserves_staged_changes(string path)
    {
        using var repo = new TestRepository(); using var reader = new RepositoryReader();
        repo.Write(path, "base\n"); repo.Write("other.txt", "other\n"); repo.Git("add", "-f", "--", path); repo.Commit("base");
        repo.Write(".gitignore", "build/\n"); repo.Commit("ignore");
        using (var tracked = new LibGit2Sharp.Repository(repo.Root)) Assert.NotNull(tracked.Index[path]);
        repo.Write(path, "staged\n"); repo.Git("add", "-f", "--", path);
        var snapshot = await reader.OpenAsync(repo.Root);
        var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index"));
        foreach (var text in new[] { "changed\n", "changed again\n", "staged\n" })
        {
            repo.Write(path, text);
            Assert.Equal($"{(text == "staged\n" ? "M " : "MM")} {path}\0",
                repo.Git("--no-optional-locks", "--literal-pathspecs", "status", "--porcelain=v1", "-z", "--", path));
            var result = await reader.RefreshAsync(snapshot, RepositoryChangeSet.Content([path]));
            Assert.False(result.Changes.IsFull);
            Assert.Same(snapshot.Files, result.Snapshot.Files);
            Assert.Same(snapshot.References, result.Snapshot.References);
            Equivalent(await reader.OpenAsync(repo.Root), result.Snapshot);
            Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")));
            Assert.Equal(text, File.ReadAllText(Path.Combine(repo.Root, path)));
            snapshot = result.Snapshot;
        }
        Assert.Single(snapshot.Changes); Assert.Equal(ComparisonKind.Index, snapshot.Changes[0].Kind);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("head")]
    [InlineData("delete")]
    [InlineData("new")]
    [InlineData("directory")]
    [InlineData("truncated")]
    public async Task Uncertain_or_structural_state_falls_back_even_if_the_hint_claims_content_only(string kind)
    {
        using var repo = new TestRepository(); using var reader = new RepositoryReader();
        repo.Write("a", "base\n"); repo.Write("b", "base\n"); repo.Commit("base");
        var previous = await reader.OpenAsync(repo.Root);
        var path = "a";
        switch (kind)
        {
            case "index": repo.Write("b", "staged\n"); repo.Git("add", "b"); break;
            case "head": repo.Git("checkout", "-b", "next"); break;
            case "delete": File.Delete(Path.Combine(repo.Root, "a")); break;
            case "new": path = "new"; repo.Write(path, "new\n"); break;
            case "directory": path = "folder"; repo.Write("folder/new", "new\n"); break;
            case "truncated": previous = previous with { FilesTruncated = true }; break;
        }
        var result = await reader.RefreshAsync(previous, RepositoryChangeSet.Content([path]));
        Assert.True(result.Changes.IsFull);
        Equivalent(await reader.OpenAsync(repo.Root), result.Snapshot);
    }

    [Fact]
    public async Task Linked_worktree_refresh_is_local_and_does_not_touch_either_index()
    {
        using var repo = new TestRepository(); using var reader = new RepositoryReader();
        repo.Write("a", "base\n"); repo.Commit("base");
        var linked = Path.Combine(repo.Container, "linked"); repo.Git("worktree", "add", "-b", "linked", linked);
        var main = await reader.OpenAsync(repo.Root); var previous = await reader.OpenAsync(linked);
        using var linkedRepo = new LibGit2Sharp.Repository(linked);
        var linkedIndexPath = Path.Combine(linkedRepo.Info.Path, "index");
        var index = File.ReadAllBytes(linkedIndexPath); var mainIndex = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index"));
        File.WriteAllText(Path.Combine(linked, "a"), "linked change\n");
        var result = await reader.RefreshAsync(previous, RepositoryChangeSet.Content(["a"]));
        Assert.False(result.Changes.IsFull);
        Equivalent(await reader.OpenAsync(linked), result.Snapshot);
        Equivalent(main, await reader.OpenAsync(repo.Root));
        Assert.Equal(index, File.ReadAllBytes(linkedIndexPath));
        Assert.Equal(mainIndex, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(repo.Root, "a")));
    }

    private static void Equivalent(RepositorySnapshot expected, RepositorySnapshot actual)
    {
        Assert.Equal(expected.Root, actual.Root); Assert.Equal(expected.Head, actual.Head);
        Assert.Equal(expected.Branch, actual.Branch); Assert.Equal(expected.IndexToken, actual.IndexToken);
        Assert.Equal(expected.Files.ToArray(), actual.Files.ToArray()); Assert.Equal(expected.Changes.ToArray(), actual.Changes.ToArray());
        Assert.Equal(expected.HistoryToken, actual.HistoryToken); Assert.Equal(expected.References?.ToArray(), actual.References?.ToArray());
        Assert.Equal(expected.Operation, actual.Operation); Assert.Equal(expected.FilesTruncated, actual.FilesTruncated);
    }
}
