using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RevisionTreeTests
{
    [Fact]
    public async Task Historical_directories_include_deleted_files_and_open_pinned_read_only_sources_without_checkout()
    {
        using var repo = new TestRepository(); repo.Write("src/中文 [x].txt", "historical\n"); repo.Write("README.md", "# Old\n"); repo.Commit("old"); var old = Head(repo);
        repo.Git("rm", "src/中文 [x].txt"); repo.Write("now.txt", "current"); repo.Commit("current"); repo.Write("untracked", "keep");
        var head = Head(repo); var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index"));
        var root = await RevisionTree.ReadAsync(repo.Root, old);
        Assert.Equal(new[] { "src", "README.md" }, root.Entries.Select(e => e.Name)); Assert.True(root.Entries[0].Directory);
        var directory = await RevisionTree.ReadAsync(repo.Root, old, "src"); Assert.Equal("src/中文 [x].txt", Assert.Single(directory.Entries).Path);
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); await model.OpenRevisionSourceAsync("src/中文 [x].txt", old);
        Assert.Equal("historical\n", model.CurrentDocument?.Text); Assert.Equal(old, model.CurrentDocument?.SourceRevision); Assert.Null(model.CurrentDocument?.Editing);
        Assert.Equal(head, Head(repo)); Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "src/中文 [x].txt"))); Assert.Equal("keep", File.ReadAllText(Path.Combine(repo.Root, "untracked")));
    }

    [Fact]
    public async Task Directory_paging_exposes_every_entry_beyond_the_first_page()
    {
        using var repo = new TestRepository();
        for (var i = 0; i < 1001; i++) repo.Write($"wide/f{i:D4}.txt", "x"); repo.Commit("wide"); var sha = Head(repo);
        var one = await RevisionTree.ReadAsync(repo.Root, sha, "wide");
        var two = await RevisionTree.ReadAsync(repo.Root, sha, "wide", 500);
        var three = await RevisionTree.ReadAsync(repo.Root, sha, "wide", 1000);
        Assert.Equal(500, one.Entries.Count); Assert.True(one.HasMore); Assert.Equal(500, two.Entries.Count); Assert.True(two.HasMore);
        Assert.Single(three.Entries); Assert.False(three.HasMore);
        Assert.Equal(1001, one.Entries.Concat(two.Entries).Concat(three.Entries).Select(e => e.Path).Distinct().Count());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("src//nested")]
    [InlineData("src\\nested")]
    public async Task Invalid_directories_and_noncommit_revisions_are_rejected(string path)
    {
        using var repo = new TestRepository(); repo.Write("a", "x"); repo.Commit("base");
        await Assert.ThrowsAsync<IOException>(() => RevisionTree.ReadAsync(repo.Root, Head(repo), path));
        await Assert.ThrowsAsync<IOException>(() => RevisionTree.ReadAsync(repo.Root, "HEAD"));
        await Assert.ThrowsAsync<IOException>(() => RevisionTree.ReadAsync(repo.Root, Head(repo), "a"));
    }

    [Fact]
    public async Task Symlinks_and_gitlinks_are_metadata_and_never_followed()
    {
        using var repo = new TestRepository(); repo.Write("a", "x"); repo.Commit("base"); var baseline = Head(repo);
        repo.Write("link", "../../outside"); repo.Git("add", "link"); var blob = repo.Git("rev-parse", ":link").Trim();
        repo.Git("update-index", "--cacheinfo", "120000", blob, "link");
        repo.Git("update-index", "--add", "--cacheinfo", "160000", baseline, "module"); repo.Git("commit", "-m", "links");
        var page = await RevisionTree.ReadAsync(repo.Root, Head(repo));
        Assert.True(page.Entries.Single(e => e.Name == "link").SymbolicLink); Assert.True(page.Entries.Single(e => e.Name == "module").Submodule);
        await Assert.ThrowsAsync<IOException>(() => RevisionTree.ReadAsync(repo.Root, Head(repo), "module"));
        Assert.Equal("../../outside", File.ReadAllText(Path.Combine(repo.Root, "link"))); Assert.False(Directory.Exists(Path.Combine(repo.Root, "module")));
    }

    [Fact]
    public async Task Late_directory_pages_cannot_replace_another_revision_directory_or_closed_browser()
    {
        var pending = new Dictionary<(string Sha, string Path, int Offset), TaskCompletionSource<RevisionTreePage>>();
        Task<RevisionTreePage> Read(string root, string sha, string path, int offset, CancellationToken token)
        {
            var source = new TaskCompletionSource<RevisionTreePage>(TaskCreationOptions.RunContinuationsAsynchronously); pending[(sha, path, offset)] = source; return source.Task;
        }
        using var browser = new RevisionBrowser(Read);
        var old = browser.OpenAsync("repo", "old"); var latest = browser.OpenAsync("repo", "new");
        pending[("new", "", 0)].SetResult(new("new", "", [new("folder", "folder", true, false)], true)); await latest;
        pending[("old", "", 0)].SetResult(new("old", "", [new("stale", "stale", false, false)], false)); await old;
        Assert.Equal("folder", Assert.Single(browser.Entries).Name); Assert.True(browser.HasMore);
        var more = browser.LoadMoreAsync(); var directory = browser.NavigateAsync("folder");
        pending[("new", "folder", 0)].SetResult(new("new", "folder", [new("folder/current", "current", false, false)], false)); await directory;
        pending[("new", "", 1)].SetResult(new("new", "", [new("stale", "stale", false, false)], false)); await more;
        Assert.Equal("folder/current", Assert.Single(browser.Entries).Path); Assert.False(browser.Loading);
        var closed = browser.NavigateAsync("pending"); var notifications = 0; browser.PropertyChanged += (_, _) => notifications++; browser.Dispose();
        pending[("new", "pending", 0)].SetResult(new("new", "pending", [new("after close", "after close", false, false)], false)); await closed;
        Assert.Equal(0, notifications); Assert.Empty(browser.Entries);
    }

    [Fact]
    public async Task Mismatched_pages_are_rejected_and_a_failed_page_can_be_retried()
    {
        var reads = 0;
        using var browser = new RevisionBrowser((root, sha, path, offset, token) =>
        {
            reads++;
            if (reads == 1) return Task.FromResult(new RevisionTreePage("wrong", path, [], false));
            return Task.FromResult(new RevisionTreePage(sha, path, [new("good", "good", false, false)], false));
        });
        await browser.OpenAsync("repo", "sha"); Assert.NotEmpty(browser.Error); Assert.Empty(browser.Entries);
        await browser.RetryAsync(); Assert.Empty(browser.Error); Assert.Equal("good", Assert.Single(browser.Entries).Path);
    }
    private static string Head(TestRepository repo) => repo.Git("rev-parse", "HEAD").Trim();
}
