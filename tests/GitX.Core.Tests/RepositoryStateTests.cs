using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RepositoryStateTests
{
    [Fact]
    public void Cached_index_checks_bytes_even_with_same_size_and_timestamp()
    {
        using var repo = new TestRepository(); repo.Write("a", "one\n"); repo.Commit("base");
        var index = Path.Combine(repo.Root, ".git", "index");
        repo.Git("update-index", "--assume-unchanged", "a");
        repo.Git("update-index", "--no-assume-unchanged", "a"); // Remove Git's optional TREE extension first.
        // Toggle a semantic flag without changing entry size; restore mtime to
        // prove that a metadata-only cache cannot hide concurrent Git edits.
        var before = RepositoryState.Token(repo.Root, []);
        var stamp = File.GetLastWriteTimeUtc(index); var size = new FileInfo(index).Length;
        repo.Git("update-index", "--assume-unchanged", "a"); File.SetLastWriteTimeUtc(index, stamp);
        Assert.Equal(size, new FileInfo(index).Length);
        Assert.NotEqual(before, RepositoryState.Token(repo.Root, []));
    }
    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    public void Stat_refresh_does_not_invalidate_state_but_staging_does(string version)
    {
        using var repo = new TestRepository();
        repo.Write("folder/a.cs", "same\n"); repo.Write("folder/another.cs", "same\n"); repo.Commit("base");
        repo.Git("update-index", "--index-version=" + version);
        var before = RepositoryState.Token(repo.Root, ["folder/a.cs"]);
        var file = Path.Combine(repo.Root, "folder/a.cs");
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(-15));
        Assert.Empty(repo.Git("status", "--porcelain"));
        Assert.Equal(before, RepositoryState.Token(repo.Root, ["folder/a.cs"]));
        repo.Write("folder/a.cs", "changed\n"); repo.Git("add", "folder/a.cs");
        Assert.NotEqual(before, RepositoryState.Token(repo.Root, ["folder/a.cs"]));
    }

    [Fact]
    public void Intent_to_add_skip_worktree_assume_unchanged_and_mode_remain_semantic()
    {
        using var repo = new TestRepository(); repo.Write("empty", ""); repo.Commit("base");
        var before = RepositoryState.Token(repo.Root, []);
        repo.Git("update-index", "--assume-unchanged", "empty");
        Assert.NotEqual(before, RepositoryState.Token(repo.Root, []));
        repo.Git("update-index", "--no-assume-unchanged", "empty");
        Assert.Equal(before, RepositoryState.Token(repo.Root, []));
        repo.Git("update-index", "--skip-worktree", "empty");
        Assert.NotEqual(before, RepositoryState.Token(repo.Root, []));
        repo.Git("update-index", "--no-skip-worktree", "empty");
        var normal = RepositoryState.Token(repo.Root, []);
        repo.Git("update-index", "--chmod=+x", "empty");
        Assert.NotEqual(normal, RepositoryState.Token(repo.Root, []));
        repo.Write("new-empty", ""); repo.Git("add", "-N", "new-empty");
        var intent = RepositoryState.Token(repo.Root, []);
        repo.Git("add", "new-empty");
        Assert.NotEqual(intent, RepositoryState.Token(repo.Root, []));
    }

    [Fact]
    public void Conflict_stages_and_resolve_undo_remain_protected()
    {
        using var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base");
        repo.Git("checkout", "-b", "side"); repo.Write("a", "side\n"); repo.Commit("side");
        repo.Git("checkout", "main"); repo.Write("a", "main\n"); repo.Commit("main");
        Assert.Throws<IOException>(() => repo.Git("merge", "side"));
        var conflict = RepositoryState.Token(repo.Root, []);
        repo.Write("a", "resolved\n"); repo.Git("add", "a");
        var resolved = RepositoryState.Token(repo.Root, []);
        Assert.NotEqual(conflict, resolved);
        repo.Git("update-index", "--clear-resolve-undo");
        Assert.NotEqual(resolved, RepositoryState.Token(repo.Root, []));
    }
}
