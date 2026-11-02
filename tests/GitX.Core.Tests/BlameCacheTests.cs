using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class BlameCacheTests
{
    [Fact]
    public async Task Proven_inputs_detect_same_size_config_edits_before_reusing_annotations()
    {
        using var repo = Fixture(); repo.Git("config", "core.abbrev", "7");
        var reader = new BlameReader(); var first = await reader.ReadAsync(repo.Root, "a.txt");
        var config = Path.Combine(repo.Root, ".git", "config");
        var original = File.ReadAllText(config); var time = File.GetLastWriteTimeUtc(config);
        File.WriteAllText(config, original.Replace("abbrev = 7", "abbrev = 8")); File.SetLastWriteTimeUtc(config, time);
        var changed = await reader.ReadAsync(repo.Root, "a.txt");
        Assert.NotSame(first.Blame, changed.Blame); MatchesGit(repo, changed);
        File.WriteAllText(config, original); File.SetLastWriteTimeUtc(config, time);
        Assert.Same(first.Blame, (await reader.ReadAsync(repo.Root, "a.txt")).Blame);
    }

    [Fact]
    public async Task Hiding_annotations_keeps_the_displayed_source_snapshot_without_a_disk_read()
    {
        using var repo = Fixture(); using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        model.SelectedEntry = model.Entries.Single(e => e.Path == "a.txt"); await model.ToggleBlameAsync();
        var before = model.CurrentDocument!; var version = model.DocumentVersion;
        repo.Write("a.txt", "changed on disk\n");
        await model.ToggleBlameAsync();
        Assert.Null(model.CurrentDocument!.Blame); Assert.Equal(before.Text, model.CurrentDocument.Text);
        Assert.Equal(before.CommentContext, model.CurrentDocument.CommentContext);
        Assert.True(model.CurrentDocument.PreserveView); Assert.True(model.DocumentVersion > version);
        await model.ToggleBlameAsync();
        Assert.Equal("changed on disk\n", model.CurrentDocument!.Text); MatchesGit(repo, model.CurrentDocument);
    }

    [Fact]
    public async Task Window_toggles_reuse_annotations_and_refresh_or_repository_open_clears_them()
    {
        using var repo = Fixture(); using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        model.SelectedEntry = model.Entries.Single(e => e.Path == "a.txt"); await model.ToggleBlameAsync();
        var first = model.CurrentDocument!.Blame; Assert.NotNull(first);
        await model.ToggleBlameAsync(); await model.ToggleBlameAsync(); Assert.Same(first, model.CurrentDocument!.Blame);
        await model.RefreshAsync(); Assert.NotSame(first, model.CurrentDocument!.Blame); Assert.Equal(first, model.CurrentDocument.Blame);
        await model.OpenAsync(repo.Root); model.SelectedEntry = model.Entries.Single(e => e.Path == "a.txt");
        if (model.BlameLabel != "注解") await model.ToggleBlameAsync();
        await model.ToggleBlameAsync();
        Assert.NotSame(first, model.CurrentDocument!.Blame); Assert.Equal(first, model.CurrentDocument.Blame);
    }

    [Fact]
    public async Task Repeated_versions_reuse_annotations_but_same_size_worktree_edits_and_new_head_do_not()
    {
        using var repo = Fixture(); var reader = new BlameReader();
        var current = await reader.ReadAsync(repo.Root, "a.txt");
        Assert.Same(current.Blame, (await reader.ReadAsync(repo.Root, "a.txt")).Blame);
        var revision = new BlameRevision(repo.Git("rev-parse", "HEAD~1").Trim(), "a.txt");
        var historical = await reader.ReadRevisionAsync(repo.Root, revision);
        Assert.Same(historical.Blame, (await reader.ReadRevisionAsync(repo.Root, revision)).Blame);
        var time = File.GetLastWriteTimeUtc(Path.Combine(repo.Root, "a.txt"));
        repo.Write("a.txt", "first\nlocal\nlast\n"); File.SetLastWriteTimeUtc(Path.Combine(repo.Root, "a.txt"), time);
        var changed = await reader.ReadAsync(repo.Root, "a.txt");
        Assert.NotSame(current.Blame, changed.Blame); Assert.False(changed.Blame![1].IsCommitted);
        // Restore a distinct stat time before asking Git add to make a commit;
        // the preceding assertion intentionally kept its old size/mtime.
        File.SetLastWriteTimeUtc(Path.Combine(repo.Root, "a.txt"), time.AddSeconds(2));
        repo.Commit("third"); var committed = await reader.ReadAsync(repo.Root, "a.txt");
        Assert.Equal(3, committed.Blame![1].CommitNumber); Assert.True(committed.Blame[1].IsCommitted);
        Assert.Equal("first\nbase!\nlast\n", (await reader.ReadRevisionAsync(repo.Root, revision)).Text);
    }

    [Fact]
    public async Task Included_config_contents_invalidate_without_relying_on_size_or_mtime()
    {
        using var repo = Fixture(); var reader = new BlameReader(); var include = Path.Combine(repo.Container, "included.config");
        File.WriteAllText(include, "[core]\n    abbrev = 7\n"); repo.Git("config", "include.path", include);
        var first = await reader.ReadAsync(repo.Root, "a.txt"); Assert.Same(first.Blame, (await reader.ReadAsync(repo.Root, "a.txt")).Blame);
        var time = File.GetLastWriteTimeUtc(include); File.WriteAllText(include, "[core]\n    abbrev = 8\n"); File.SetLastWriteTimeUtc(include, time);
        var second = await reader.ReadAsync(repo.Root, "a.txt"); Assert.NotSame(first.Blame, second.Blame); Assert.Equal(first.Blame, second.Blame);
        Assert.Same(second.Blame, (await reader.ReadAsync(repo.Root, "a.txt")).Blame);
    }

    [Fact]
    public async Task Mailmap_files_and_ignored_revision_files_are_read_fresh()
    {
        using var repo = Fixture(); var reader = new BlameReader(); var map = Path.Combine(repo.Container, "authors.map");
        repo.Git("config", "mailmap.file", map);
        File.WriteAllText(map, "First Map <first@example.invalid> GitX Test <gitx-test@example.invalid>\n");
        var first = await reader.ReadAsync(repo.Root, "a.txt"); Assert.Equal("First Map", first.Blame![0].Author);
        File.WriteAllText(map, "Other Map <other@example.invalid> GitX Test <gitx-test@example.invalid>\n");
        var other = await reader.ReadAsync(repo.Root, "a.txt"); Assert.Equal("Other Map", other.Blame![0].Author); Assert.NotSame(first.Blame, other.Blame);
        repo.Git("config", "--unset", "mailmap.file"); var ignored = Path.Combine(repo.Container, "ignored-revisions");
        File.WriteAllText(ignored, ""); repo.Git("config", "blame.ignoreRevsFile", ignored);
        first = await reader.ReadAsync(repo.Root, "a.txt"); File.WriteAllText(ignored, repo.Git("rev-parse", "HEAD"));
        other = await reader.ReadAsync(repo.Root, "a.txt");
        Assert.NotSame(first.Blame, other.Blame); MatchesGit(repo, other);
    }

    [Fact]
    public async Task Active_diff_attributes_bypass_cache_and_changed_attributes_are_rechecked()
    {
        using var repo = Fixture(); var reader = new BlameReader();
        await reader.PrepareAsync(repo.Root);
        var normal = await reader.ReadAsync(repo.Root, "a.txt"); Assert.Same(normal.Blame, (await reader.ReadAsync(repo.Root, "a.txt")).Blame);
        repo.Write(".gitattributes", "a.txt diff=fixture\n");
        var attributed = await reader.ReadAsync(repo.Root, "a.txt"); var again = await reader.ReadAsync(repo.Root, "a.txt");
        Assert.NotSame(normal.Blame, attributed.Blame); Assert.NotSame(attributed.Blame, again.Blame); MatchesGit(repo, again);
        File.Delete(Path.Combine(repo.Root, ".gitattributes"));
        Assert.Same(normal.Blame, (await reader.ReadAsync(repo.Root, "a.txt")).Blame);
    }

    [Fact]
    public async Task Replaced_history_is_not_hidden_by_an_existing_cache_entry()
    {
        using var repo = Fixture(); var reader = new BlameReader();
        var original = await reader.ReadAsync(repo.Root, "a.txt"); var head = repo.Git("rev-parse", "HEAD").Trim();
        var replacement = repo.Git("commit-tree", head + "^{tree}", "-m", "replacement root").Trim();
        repo.Git("replace", head, replacement);
        var changed = await reader.ReadAsync(repo.Root, "a.txt"); Assert.NotSame(original.Blame, changed.Blame);
        Assert.All(changed.Blame!, line => Assert.Equal(1, line.CommitNumber)); MatchesGit(repo, changed);
        replacement = repo.Git("commit-tree", head + "^{tree}", "-m", "another replacement root").Trim();
        repo.Git("replace", "-f", head, replacement);
        var next = await reader.ReadAsync(repo.Root, "a.txt"); Assert.NotSame(changed.Blame, next.Blame); Assert.NotEqual(changed.Blame![0].Summary, next.Blame![0].Summary);
    }

    [Fact]
    public async Task Cache_is_bounded_lru_and_clear_or_other_reader_does_not_share_entries()
    {
        using var repo = new TestRepository(); foreach (var path in new[] { "a", "b", "c", "d", "e" }) repo.Write(path, "one\n"); repo.Commit("five files");
        var reader = new BlameReader(); var a = await reader.ReadAsync(repo.Root, "a"); var b = await reader.ReadAsync(repo.Root, "b");
        await reader.ReadAsync(repo.Root, "c"); await reader.ReadAsync(repo.Root, "d"); Assert.Same(a.Blame, (await reader.ReadAsync(repo.Root, "a")).Blame);
        await reader.ReadAsync(repo.Root, "e"); Assert.NotSame(b.Blame, (await reader.ReadAsync(repo.Root, "b")).Blame);
        var beforeClear = await reader.ReadAsync(repo.Root, "a"); reader.ClearCache(); var after = await reader.ReadAsync(repo.Root, "a");
        Assert.NotSame(beforeClear.Blame, after.Blame); Assert.Equal(beforeClear.Blame, after.Blame);
        Assert.NotSame(after.Blame, (await new BlameReader().ReadAsync(repo.Root, "a")).Blame);
    }

    [Fact]
    public async Task Oversized_annotation_metadata_is_not_retained()
    {
        using var repo = new TestRepository(); repo.Git("config", "user.name", new string('A', 600));
        repo.Write("a.txt", string.Join('\n', Enumerable.Range(1, 4000).Select(i => $"line {i}"))); repo.Commit("large metadata");
        var reader = new BlameReader(); var first = await reader.ReadAsync(repo.Root, "a.txt"); var second = await reader.ReadAsync(repo.Root, "a.txt");
        Assert.NotSame(first.Blame, second.Blame); Assert.Equal(first.Blame, second.Blame); Assert.Equal(4000, second.Blame!.Count);
    }

    [Fact]
    public async Task Cancelling_one_read_does_not_poison_or_cancel_another_read_or_a_cache_hit()
    {
        using var repo = Fixture(); var reader = new BlameReader(); using var cancelled = new CancellationTokenSource();
        var pending = reader.ReadAsync(repo.Root, "a.txt", cancelled.Token); cancelled.Cancel();
        var valid = reader.ReadAsync(repo.Root, "a.txt"); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var document = await valid; Assert.Equal(3, document.Blame!.Count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(repo.Root, "a.txt", cancelled.Token));
        Assert.Same(document.Blame, (await reader.ReadAsync(repo.Root, "a.txt")).Blame);
    }

    private static TestRepository Fixture()
    {
        var repo = new TestRepository(); repo.Write("a.txt", "first\nbase!\nlast\n"); repo.Commit("first");
        repo.Write("a.txt", "first\nother\nlast\n"); repo.Commit("second"); return repo;
    }
    private static void MatchesGit(TestRepository repo, RepositoryDocument document)
    {
        var expected = BlameReader.Parse(repo.Git("blame", "--porcelain", "--", "a.txt"));
        Assert.Equal(expected, document.Blame!.Select(line => line with { CommitNumber = null }));
    }
}
