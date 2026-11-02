using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryTests
{
    [Fact]
    public async Task Literal_path_filter_matches_full_tree_diff_for_roots_merges_moves_modes_and_type_changes()
    {
        using var fixture = new TestRepository();
        fixture.Write("src/[literal].txt", "base"); fixture.Write("other.txt", "other"); fixture.Commit("root");
        fixture.Git("switch", "-c", "topic"); fixture.Write("src/topic.txt", "topic"); fixture.Commit("side");
        fixture.Git("switch", "main"); fixture.Write("other.txt", "main"); fixture.Commit("main");
        fixture.Git("merge", "--no-ff", "topic", "-m", "merge");
        fixture.Git("mv", "src/[literal].txt", "src/moved.txt"); fixture.Commit("rename");
        fixture.Git("update-index", "--chmod=+x", "src/moved.txt"); fixture.Git("commit", "-m", "mode");
        fixture.Git("rm", "src/topic.txt"); fixture.Commit("delete");
        fixture.Git("rm", "other.txt"); fixture.Write("other.txt/child.txt", "type change"); fixture.Commit("file to tree");
        fixture.Git("update-index", "--add", "--cacheinfo", "160000,0123456789012345678901234567890123456789,submodule"); fixture.Git("commit", "-m", "gitlink");
        using var native = new LibGit2Sharp.Repository(fixture.Root);
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(fixture.Root);
        var heads = Assert.IsType<string>(snapshot.HistoryHeads);
        foreach (var path in new[] { "src", "src/[literal].txt", "src/moved.txt", "src/topic.txt", "other.txt", "other.txt/child.txt", "submodule", "missing", "Src", "src/../other.txt", "src//moved.txt", "./src", "/src", ".", "src/moved.txt/nested", "src/\0missing" })
        {
            var expected = native.Commits.QueryBy(new LibGit2Sharp.CommitFilter { IncludeReachableFrom = heads.Split(' '), SortBy = LibGit2Sharp.CommitSortStrategies.Topological | LibGit2Sharp.CommitSortStrategies.Time })
                .Where(commit => { using var diff = native.Diff.Compare<LibGit2Sharp.TreeChanges>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree, new LibGit2Sharp.CompareOptions { Similarity = LibGit2Sharp.SimilarityOptions.None });
                    return diff.Any(change => change.Path == path || change.Path?.StartsWith(path + "/", StringComparison.Ordinal) == true || change.OldPath == path || change.OldPath?.StartsWith(path + "/", StringComparison.Ordinal) == true); }).Select(c => c.Sha).ToArray();
            Assert.Equal(expected, (await reader.QueryHistoryAsync(fixture.Root, snapshot.HistoryHeads, new(Path: path))).Commits.Select(c => c.Sha));
        }
    }

    [Fact]
    public async Task Path_filtered_pages_and_cancelled_resume_keep_each_match_once()
    {
        using var repo = new TestRepository();
        for (var i = 0; i < 105; i++) { repo.Write("src/a", i.ToString()); repo.Commit("match " + i); }
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        var query = new HistoryQuery(Path: "src"); var first = await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, query);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, query, 100, cancelled.Token));
        var second = await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, query, 100);
        Assert.True(first.HasMore); Assert.False(second.HasMore); Assert.Equal(5, second.Commits.Count);
        Assert.Equal(105, first.Commits.Concat(second.Commits).Select(c => c.Sha).Distinct().Count());
    }

    [Fact]
    public async Task Reference_labels_preserve_kind_and_invalidate_same_name_replacement()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base");
        repo.Git("branch", "release"); repo.Git("tag", "release");
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        var entry = Assert.Single((await reader.HistoryAsync(repo.Root, snapshot.HistoryHeads)).Commits);
        Assert.Contains(entry.ReferenceLabels!, r => r.Kind == "head" && r.CanonicalName == "HEAD");
        Assert.Contains(entry.ReferenceLabels!, r => r.Kind == "remote" && r.CanonicalName == "refs/remotes/origin/main");
        Assert.Equal(new[] { "local", "tag" }, entry.ReferenceLabels!.Where(r => r.Name == "release").Select(r => r.Kind).Order());
        repo.Git("branch", "-D", "release");
        entry = Assert.Single((await reader.HistoryAsync(repo.Root, snapshot.HistoryHeads)).Commits);
        Assert.Equal("tag", Assert.Single(entry.ReferenceLabels!, r => r.Name == "release").Kind);
        repo.Git("tag", "-d", "release"); repo.Git("branch", "release");
        entry = Assert.Single((await reader.HistoryAsync(repo.Root, snapshot.HistoryHeads)).Commits);
        Assert.Equal("local", Assert.Single(entry.ReferenceLabels!, r => r.Name == "release").Kind);
    }

    [Fact]
    public async Task All_refs_topology_matches_git_and_paging_keeps_lanes()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base");
        repo.Git("checkout", "-b", "topic"); repo.Write("b.txt", "topic"); repo.Commit("topic");
        repo.Git("checkout", "main"); repo.Write("a.txt", "main"); repo.Commit("main");
        repo.Git("merge", "--no-ff", "topic", "-m", "merge");
        repo.Git("branch", "unmerged"); repo.Git("checkout", "unmerged"); repo.Write("c.txt", "side"); repo.Commit("side tip"); repo.Git("checkout", "main");
        for (var i = 0; i < 101; i++) repo.Git("commit", "--allow-empty", "-m", $"padding {i}");
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        var first = await reader.HistoryAsync(repo.Root, snapshot.HistoryHeads); var second = await reader.HistoryAsync(repo.Root, snapshot.HistoryHeads, 100);
        var all = first.Commits.Concat(second.Commits).ToArray();
        var expected = repo.Git("log", "--all", "--format=%H %P").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToDictionary(l => l.Split(' ')[0], l => l.Trim().Split(' ').Skip(1).ToArray());
        Assert.Equal(expected.Count, all.Length); Assert.Equal(all.Length, all.Select(c => c.Sha).Distinct().Count());
        var graph = new HistoryGraph(); var positions = all.Select((c, i) => (c.Sha, i)).ToDictionary(x => x.Sha, x => x.i);
        foreach (var commit in all)
        {
            Assert.Equal(expected[commit.Sha], commit.Parents);
            foreach (var parent in commit.Parents!) Assert.True(positions[parent] > positions[commit.Sha]);
            var row = graph.Append(commit.Sha, commit.Parents!);
            Assert.Equal(row.Lane, commit.Graph!.Lane); Assert.Equal(row.Edges, commit.Graph.Edges);
        }
        Assert.Contains(all, c => c.References.Contains("unmerged")); Assert.Contains(all, c => c.Graph!.Merge);
        Assert.Contains(all, c => c.Graph!.Lane > 0);
        var again = await reader.HistoryAsync(repo.Root, snapshot.HistoryHeads, 100);
        Assert.Equal(second.Commits, again.Commits);
        using var fresh = new RepositoryReader();
        var independent = await fresh.HistoryAsync(repo.Root, snapshot.HistoryHeads, 100);
        Assert.Equal(second.Commits.Select(c => (c.Sha, c.Graph!.Lane)), independent.Commits.Select(c => (c.Sha, c.Graph!.Lane)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.HistoryAsync(repo.Root, snapshot.HistoryHeads, 100, cancelled.Token));
        Assert.Equal(second.Commits, (await reader.HistoryAsync(repo.Root, snapshot.HistoryHeads, 100)).Commits);
        // Search traverses beyond the already loaded first page and pages matches, not source rows.
        Assert.Single((await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, new(Text: "padding 0"))).Commits);
        var matched = await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, new(Text: "padding"));
        var matchedNext = await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, new(Text: "padding"), 100);
        Assert.Equal(100, matched.Commits.Count); Assert.True(matched.HasMore);
        Assert.Single(matchedNext.Commits); Assert.False(matchedNext.HasMore);
        Assert.Equal(101, matched.Commits.Concat(matchedNext.Commits).Select(c => c.Sha).Distinct().Count());
        Assert.Single((await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, new(Text: all[^1].Sha[..12]))).Commits);
    }

    [Fact]
    public async Task History_filters_combine_full_message_author_date_and_literal_changed_path()
    {
        using var repo = new TestRepository();
        repo.Write("src/[name].txt", "one"); repo.Commit("one\n\nunique body");
        var first = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("src/name.txt", "two"); repo.Commit("two");
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        var source = (await reader.HistoryAsync(repo.Root, snapshot.HistoryHeads)).Commits.Single(c => c.Sha == first);
        var query = new HistoryQuery("unique body", "gitx-test@example.invalid", source.When, source.When.AddSeconds(1), "src/[name].txt");
        Assert.Equal(first, Assert.Single((await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, query)).Commits).Sha);
        Assert.Empty((await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, query with { From = null, Until = source.When })).Commits);
        Assert.Empty((await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, query with { Author = "other" })).Commits);
        Assert.Equal(2, (await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, new(Path: "src/"))).Commits.Count);
        Assert.Single((await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, new(Path: "src/[name].txt"))).Commits);
        Assert.Empty((await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, new(Path: "Src"))).Commits);
    }

    [Fact]
    public async Task Version_comparison_reads_exact_trees_and_worktree_including_untracked_without_writing()
    {
        using var repo = new TestRepository();
        repo.Write("a.txt", "base\n"); repo.Write("deleted.txt", "deleted\n"); repo.Commit("base");
        var old = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a.txt", "committed\n"); repo.Git("rm", "deleted.txt"); repo.Write("added.txt", "added\n"); repo.Commit("next");
        var next = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a.txt", "working\r\n"); repo.Write("untracked [x].txt", "untracked\n");
        repo.Write("staged.txt", "index\n"); repo.Git("add", "staged.txt"); repo.Write("staged.txt", "worktree\n");
        var index = repo.Git("ls-files", "--stage");
        using var reader = new RepositoryReader();
        var changes = await reader.CompareChangesAsync(repo.Root, old, next);
        Assert.Equal(new[] { "a.txt", "added.txt", "deleted.txt" }, changes.Select(c => c.Path).Order());
        var committed = await reader.DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.RevisionRange, next, BaseCommit: old));
        Assert.Equal("base\n", committed.Diff!.Old.Text); Assert.Equal("committed\n", committed.Diff.New.Text);
        var deleted = await reader.DocumentAsync(new(repo.Root, "deleted.txt", ComparisonKind.RevisionRange, next, BaseCommit: old));
        Assert.False(deleted.Diff!.New.Exists);
        var local = await reader.CompareChangesAsync(repo.Root, old, null);
        Assert.Contains(local, c => c.Path == "untracked [x].txt" && c.Status == "Added");
        Assert.Contains(local, c => c.Path == "staged.txt");
        var working = await reader.DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.CommitWorktree, old));
        Assert.Equal("base\n", working.Diff!.Old.Text); Assert.Equal("working\r\n", working.Diff.New.Text);
        var staged = await reader.DocumentAsync(new(repo.Root, "staged.txt", ComparisonKind.CommitWorktree, old));
        Assert.False(staged.Diff!.Old.Exists); Assert.Equal("worktree\n", staged.Diff.New.Text);
        Assert.False(working.CanStage); Assert.False(working.CanInclude);
        Assert.Equal(index, repo.Git("ls-files", "--stage")); Assert.Equal(next, repo.Git("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Moving_non_head_ref_invalidates_history_snapshot()
    {
        using var repo = new TestRepository(); repo.Write("a", "one"); repo.Commit("one");
        using var reader = new RepositoryReader(); var before = await reader.OpenAsync(repo.Root);
        var oldPage = await reader.HistoryAsync(repo.Root, before.HistoryHeads);
        repo.Git("branch", "topic"); var after = await reader.OpenAsync(repo.Root);
        Assert.Equal(before.Head, after.Head); Assert.NotEqual(before.HistoryToken, after.HistoryToken);
        Assert.Equal(before.HistoryHeads, after.HistoryHeads);
        Assert.DoesNotContain("topic", oldPage.Commits[0].References);
        Assert.Contains("topic", (await reader.HistoryAsync(repo.Root, after.HistoryHeads)).Commits[0].References);
        using var other = new TestRepository(); other.Write("b", "two"); other.Commit("other");
        var otherSnapshot = await reader.OpenAsync(other.Root);
        Assert.Equal(otherSnapshot.Head, (await reader.HistoryAsync(other.Root, otherSnapshot.HistoryHeads)).Commits[0].Sha);
        reader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.HistoryAsync(repo.Root, after.HistoryHeads));
    }
}
