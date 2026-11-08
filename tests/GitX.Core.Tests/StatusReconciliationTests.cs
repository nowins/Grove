using GitX.Core;
using LibGit2Sharp;
using Xunit;

namespace GitX.Core.Tests;

public sealed class StatusReconciliationTests
{
    [Theory]
    [InlineData("changed\n", FileStatus.ModifiedInWorkdir, "MM")]
    [InlineData("staged\n", FileStatus.Unaltered, "M ")]
    public void Contradictory_native_rows_are_rechecked_as_one_literal_tracked_file(string text, FileStatus worktree, string porcelain)
    {
        using var fixture = new TestRepository();
        const string path = "目录/[literal].txt";
        fixture.Write(path, "base\n"); fixture.Write("目录/l.txt", "base\n"); fixture.Commit("base");
        fixture.Write(path, "staged\n"); fixture.Git("--literal-pathspecs", "add", "--", path);
        fixture.Write(path, text); fixture.Write("目录/l.txt", "other\n");
        var head = fixture.Git("rev-parse", "HEAD");
        var index = File.ReadAllBytes(Path.Combine(fixture.Root, ".git", "index"));
        using var repo = new Repository(fixture.Root);
        StatusReadEntry[] reported = [
            new(path, FileStatus.ModifiedInIndex | FileStatus.DeletedFromWorkdir),
            new("目录/l.txt", FileStatus.ModifiedInWorkdir),
            new(path, FileStatus.NewInWorkdir)];

        var actual = RepositoryReader.ReconcileStatus(repo, reported);

        Assert.Equal(new[] {
            new StatusReadEntry(path, FileStatus.ModifiedInIndex | worktree),
            new StatusReadEntry("目录/l.txt", FileStatus.ModifiedInWorkdir) }, actual);
        Assert.Equal($"{porcelain} {path}\0", fixture.Git("--no-optional-locks", "--literal-pathspecs", "status", "--porcelain=v1", "-z", "--", path));
        Assert.Equal(head, fixture.Git("rev-parse", "HEAD"));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Root, ".git", "index")));
        Assert.Equal(text, File.ReadAllText(Path.Combine(fixture.Root, path)));
        Assert.Equal("other\n", File.ReadAllText(Path.Combine(fixture.Root, "目录/l.txt")));
    }

    [Fact]
    public void Clean_literal_file_removes_both_false_worktree_rows()
    {
        using var fixture = new TestRepository();
        fixture.Write("目录/[literal].txt", "base\n"); fixture.Commit("base");
        using var repo = new Repository(fixture.Root);
        StatusReadEntry[] reported = [new("目录/[literal].txt", FileStatus.DeletedFromWorkdir), new("目录/[literal].txt", FileStatus.NewInWorkdir)];
        Assert.Empty(RepositoryReader.ReconcileStatus(repo, reported));
        Assert.Equal("", fixture.Git("--no-optional-locks", "status", "--porcelain=v1", "-z"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("untracked")]
    [InlineData("directory")]
    [InlineData("symlink-index")]
    [InlineData("conflict")]
    [InlineData("rename")]
    [InlineData("index-disagreement")]
    public void Unproven_or_structural_states_are_not_collapsed(string kind)
    {
        using var fixture = new TestRepository();
        fixture.Write("a.txt", "base\n"); fixture.Commit("base");
        var path = kind == "untracked" ? "new.txt" : "a.txt";
        fixture.Write(path, "changed\n");
        switch (kind)
        {
            case "missing": File.Delete(Path.Combine(fixture.Root, path)); break;
            case "directory": File.Delete(Path.Combine(fixture.Root, path)); fixture.Write(path + "/child", "child\n"); break;
            case "symlink-index": fixture.Git("update-index", "--cacheinfo", "120000", fixture.Git("rev-parse", "HEAD:a.txt").Trim(), path); break;
        }
        var state = FileStatus.DeletedFromWorkdir | (kind == "conflict" ? FileStatus.Conflicted : FileStatus.Unaltered) |
            (kind == "index-disagreement" ? FileStatus.ModifiedInIndex : FileStatus.Unaltered);
        StatusReadEntry[] reported = [new(path, state, WorktreeOldPath: kind == "rename" ? "old.txt" : null), new(path, FileStatus.NewInWorkdir)];
        using var repo = new Repository(fixture.Root);
        var head = fixture.Git("rev-parse", "HEAD");
        var index = File.ReadAllBytes(Path.Combine(fixture.Root, ".git", "index"));
        Assert.Same(reported, RepositoryReader.ReconcileStatus(repo, reported));
        Assert.Equal(head, fixture.Git("rev-parse", "HEAD"));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Root, ".git", "index")));
    }

    [Fact]
    public void Case_only_path_differences_are_not_the_same_literal_path()
    {
        using var fixture = new TestRepository();
        fixture.Write("a.txt", "base\n"); fixture.Commit("base");
        using var repo = new Repository(fixture.Root);
        StatusReadEntry[] reported = [new("a.txt", FileStatus.DeletedFromWorkdir), new("A.txt", FileStatus.NewInWorkdir)];
        Assert.Same(reported, RepositoryReader.ReconcileStatus(repo, reported));
    }
}
