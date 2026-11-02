using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryLogControlsTests
{
    [Fact]
    public async Task Text_options_search_full_messages_and_preserve_sha_prefix_lookup()
    {
        using var repo = new TestRepository();
        repo.Write("src/A.cs", "one"); repo.Commit("Fix: Alpha\n\nBody marker");
        var first = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("src/A.cs", "two"); repo.Commit("fix: beta");
        using var reader = new RepositoryReader(); var snapshot = await reader.OpenAsync(repo.Root);
        async Task<HistoryPage> Search(HistoryQuery query) => await reader.QueryHistoryAsync(repo.Root, snapshot.HistoryHeads, query);
        Assert.Equal(2, (await Search(new("fix:"))).Commits.Count);
        Assert.Single((await Search(new("Fix:", MatchCase: true))).Commits);
        Assert.Equal(first, Assert.Single((await Search(new("Body m.*r", UseRegex: true))).Commits).Sha);
        Assert.Empty((await Search(new("body m.*r", UseRegex: true, MatchCase: true))).Commits);
        Assert.Empty((await Search(new("Body m.*r"))).Commits);
        Assert.Equal(2, (await Search(new("^fix: (alpha|beta)", UseRegex: true, Path: "src"))).Commits.Count);
        Assert.Equal(first, Assert.Single((await Search(new(first[..12].ToUpperInvariant(), MatchCase: true))).Commits).Sha);
        await Assert.ThrowsAsync<IOException>(() => Search(new("[", UseRegex: true)));
        Assert.Equal(2, (await Search(new("fix:"))).Commits.Count);
    }

    [Fact]
    public async Task Invalid_regex_keeps_active_results_and_clear_resets_options()
    {
        using var repo = new TestRepository(); repo.Write("src/a.txt", "one"); repo.Commit("Fix");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        model.HistoryText = "^Fix$"; model.HistoryUseRegex = model.HistoryMatchCase = true;
        await model.ApplyHistoryQueryAsync(); var rows = model.Commits;
        model.HistoryText = "["; await model.ApplyHistoryQueryAsync();
        Assert.Contains("正则无效", model.Error); Assert.Same(rows, model.Commits);
        await model.ApplyHistoryQueryAsync(clear: true);
        Assert.Equal("", model.Error); Assert.False(model.HistoryUseRegex); Assert.False(model.HistoryMatchCase); Assert.Single(model.Commits);
        Assert.Throws<IOException>(() => HistoryQueryInput.Create(new string('a', 2049), "", "", "", "", useRegex: true));
    }

    [Fact]
    public async Task Branch_search_retains_hierarchy_exact_identity_and_active_scope()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "one"); repo.Commit("base");
        repo.Git("branch", "feature/a"); repo.Git("tag", "feature/a");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.SelectHistoryReferenceAsync("refs/heads/feature/a");
        var notices = new List<string?>(); model.PropertyChanged += (_, e) => notices.Add(e.PropertyName);
        model.HistoryBranchSearch = "feature/a";
        static IEnumerable<HistoryBranchNode> Flatten(IEnumerable<HistoryBranchNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
        var nodes = Flatten(model.HistoryBranches).ToArray();
        Assert.Contains(nodes, n => n.IsGroup && n.Name == "feature");
        Assert.Contains(nodes, n => n.Key == "refs/heads/feature/a"); Assert.Contains(nodes, n => n.Key == "refs/tags/feature/a");
        model.HistoryBranchSearch = "missing";
        Assert.Equal("refs/heads/feature/a", model.HistoryReferenceKey); Assert.True(model.HasHistoryBranchFilter);
        Assert.Contains(model.HistoryReferenceCandidates, r => r.CanonicalName == "refs/tags/feature/a");
        await model.SelectHistoryReferenceAsync(null);
        Assert.Contains(nameof(MainViewModel.HistoryReferenceKey), notices); Assert.False(model.HasHistoryBranchFilter);
    }

    [Fact]
    public void History_tree_flat_switch_and_bulk_expansion_keep_original_comparison_entries()
    {
        var nav = new FileNavigation();
        var entries = new[] { new NavigationEntry("src/deep/a.cs", "Deleted", ComparisonKind.RevisionRange, Commit: "new", Status: "Deleted", BaseCommit: "old"),
            new NavigationEntry("src/deep/b.cs", "Added", ComparisonKind.RevisionRange, Commit: "new", Status: "Added", BaseCommit: "old") };
        nav.Update("repo", entries, 2, false, null);
        Assert.Contains(nav.Rows, r => r.Name == "src/deep" && r.IsDirectory);
        nav.ExpandAll(false); Assert.DoesNotContain(nav.Rows, r => r.IsFile);
        nav.ExpandAll(true); Assert.Equal(entries, nav.Rows.Where(r => r.IsFile).Select(r => r.Entry));
        nav.Update("repo", entries, 3, false, null);
        Assert.Equal(entries.Select(e => e.Path), nav.Rows.Select(r => r.Name)); Assert.Same(entries[0], nav.Rows[0].Entry);
        nav.Update("repo", entries, 2, false, null, compactDirectories: false);
        Assert.Contains(nav.Rows, r => r.Name == "src"); Assert.Contains(nav.Rows, r => r.Name == "deep");
        Assert.True(nav.Rows.Single(r => r.Entry == entries[0]).IsDeleted);
        Assert.True(nav.Rows.Single(r => r.Entry == entries[1]).IsAdded);
    }
}
