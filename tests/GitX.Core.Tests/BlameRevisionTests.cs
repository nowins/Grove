using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class BlameRevisionTests
{
    [Fact]
    public void Annotation_date_uses_the_local_calendar_day()
    {
        var time = new DateTimeOffset(2026, 10, 27, 23, 30, 0, TimeSpan.Zero);
        var text = $"{new string('a', 40)} 1 1 1\nauthor Author\nauthor-time {time.ToUnixTimeSeconds()}\nfilename a.cs\n\tline\n";
        Assert.Equal(time.ToLocalTime().ToString("yyyy-MM-dd"), Assert.Single(BlameReader.Parse(text)).When);
    }

    [Fact]
    public async Task Previous_revision_follows_rename_and_maps_inserted_lines_without_writing()
    {
        using var repo = new TestRepository();
        const string oldPath = "旧 名.java", path = "新 名.java";
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n";
        repo.Write(oldPath, original); repo.Commit("initial"); var first = repo.Git("rev-parse", "HEAD").Trim();
        repo.Git("mv", oldPath, path);
        var renamed = original.Replace("line 20\n", "inserted one\ninserted two\nline 20\n");
        repo.Write(path, renamed); repo.Commit("rename and insert"); var second = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write(path, renamed.Replace("line 30\n", "new tail\n")); repo.Commit("replace tail");
        var head = repo.Git("rev-parse", "HEAD"); var index = repo.Git("write-tree");
        repo.Write(path, File.ReadAllText(Path.Combine(repo.Root, path)).Replace("line 2\n", "local\n"));
        var working = File.ReadAllText(Path.Combine(repo.Root, path));
        var reader = new BlameReader(); var source = await reader.ReadAsync(repo.Root, path);
        var inserted = source.Blame!.Single(line => line.Line == 20);
        Assert.Equal(second, inserted.Sha); Assert.Equal(first, inserted.PreviousSha); Assert.Equal(oldPath, inserted.PreviousPath);
        var target = await reader.PreviousRevisionAsync(repo.Root, inserted, source.Text);
        Assert.Equal(new BlameRevision(first, oldPath, 20), target);
        var previous = await reader.ReadRevisionAsync(repo.Root, target);
        Assert.Equal(original, previous.Text); Assert.Equal(first, previous.SourceRevision); Assert.Equal(oldPath, previous.Path);
        Assert.Null(previous.CommentContext); Assert.Null(previous.Diff); Assert.All(previous.Blame!, line => Assert.False(line.HasPrevious));
        var plain = await reader.ReadRevisionAsync(repo.Root, target, annotate: false);
        Assert.Null(plain.Blame); Assert.Equal(original, plain.Text); Assert.Equal(first, plain.SourceRevision);
        var uncommitted = source.Blame![1]; Assert.False(uncommitted.IsCommitted); Assert.True(uncommitted.HasPrevious);
        var headTarget = await reader.PreviousRevisionAsync(repo.Root, uncommitted, source.Text);
        Assert.Equal(head.Trim(), headTarget.Sha); Assert.Equal(2, headTarget.Line);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("write-tree"));
        Assert.Equal(working, File.ReadAllText(Path.Combine(repo.Root, path)));
    }

    [Fact]
    public async Task Commit_numbers_include_replaced_revisions_and_follow_renames()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "survives\nbase\n"); repo.Commit("initial");
        repo.Write("a.txt", "survives\nintermediate\n"); repo.Commit("intermediate");
        repo.Write("a.txt", "survives\nfinal\n"); repo.Commit("final");
        repo.Git("mv", "a.txt", "b.txt"); repo.Commit("rename");
        var reader = new BlameReader(); var doc = await reader.ReadAsync(repo.Root, "b.txt");
        Assert.Equal(1, doc.Blame![0].CommitNumber); Assert.Equal(3, doc.Blame[1].CommitNumber);
        var atFinal = await reader.ReadRevisionAsync(repo.Root, new(repo.Git("rev-parse", "HEAD~1").Trim(), "a.txt"));
        Assert.Equal(3, atFinal.Blame![1].CommitNumber);
    }

    [Fact]
    public async Task Historical_new_file_empty_binary_and_limit_keep_their_meaning()
    {
        using var repo = new TestRepository(); repo.Write("other", "base\n"); repo.Commit("base");
        repo.Write("new.txt", "new\n"); repo.Write("empty", ""); repo.Write("binary", "x\0y");
        repo.Write("large", string.Join('\n', Enumerable.Repeat("line", 5001))); repo.Commit("add files");
        var sha = repo.Git("rev-parse", "HEAD").Trim(); var reader = new BlameReader();
        var added = await reader.ReadRevisionAsync(repo.Root, new(sha, "new.txt")); Assert.False(added.Blame![0].HasPrevious);
        await Assert.ThrowsAsync<IOException>(() => reader.PreviousRevisionAsync(repo.Root, added.Blame[0], added.Text));
        Assert.Empty((await reader.ReadRevisionAsync(repo.Root, new(sha, "empty"))).Blame!);
        Assert.True((await reader.ReadRevisionAsync(repo.Root, new(sha, "binary"))).IsNotice);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadRevisionAsync(repo.Root, new(sha, "large")));
        await Assert.ThrowsAsync<IOException>(() => reader.ReadRevisionAsync(repo.Root, new(sha, "missing")));
    }

    [Fact]
    public async Task Previous_annotations_open_distinct_tabs_reuse_revisions_and_keep_per_tab_blame()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "one\n"); repo.Write("b.cs", "other\n"); repo.Commit("first");
        var first = repo.Git("rev-parse", "HEAD").Trim(); repo.Write("a.cs", "two\n"); repo.Commit("second");
        var second = repo.Git("rev-parse", "HEAD").Trim(); repo.Write("a.cs", "three\n"); repo.Commit("third");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        model.SelectedEntry = model.Entries.Single(e => e.Path == "a.cs"); await model.ToggleBlameAsync();
        var workingVersion = model.DocumentVersion;
        var workingId = model.ActiveSourceTabId!;
        await model.PreviousAnnotationAsync(1, workingVersion);
        var secondId = model.ActiveSourceTabId!;
        Assert.Equal(2, model.SourceTabs.Count); Assert.Equal(second, model.CurrentDocument!.SourceRevision); Assert.Equal("two\n", model.CurrentDocument.Text);
        Assert.Equal(1, model.CurrentDocument.RevealLine); Assert.False(model.CurrentDocument.PreserveView);
        await model.PreviousAnnotationAsync(1, workingVersion); Assert.Equal(second, model.CurrentDocument.SourceRevision);
        await model.PreviousAnnotationAsync(1, model.DocumentVersion); Assert.Equal(first, model.CurrentDocument!.SourceRevision);
        var firstId = model.ActiveSourceTabId!; Assert.Equal(3, model.SourceTabs.Count);
        await model.PreviousAnnotationAsync(1, model.DocumentVersion); Assert.Equal(first, model.CurrentDocument.SourceRevision);
        await model.ToggleBlameAsync(); Assert.Null(model.CurrentDocument!.Blame); Assert.Equal(first, model.CurrentDocument.SourceRevision); Assert.True(model.CurrentDocument.PreserveView);
        await model.SelectSourceTabAsync(secondId, model.DocumentVersion); Assert.Equal(second, model.CurrentDocument!.SourceRevision); Assert.NotNull(model.CurrentDocument.Blame);
        await model.SelectSourceTabAsync(firstId, model.DocumentVersion); Assert.Null(model.CurrentDocument!.Blame);
        await model.SelectSourceTabAsync(workingId, model.DocumentVersion); Assert.Null(model.CurrentDocument!.SourceRevision); Assert.Equal("three\n", model.CurrentDocument.Text);
        await model.PreviousAnnotationAsync(1, model.DocumentVersion);
        Assert.Equal(3, model.SourceTabs.Count); Assert.Equal(secondId, model.ActiveSourceTabId);
        model.SelectedEntry = model.Entries.Single(e => e.Path == "a.cs"); await UntilAsync(() => model.CurrentDocument is not null);
        Assert.Equal(workingId, model.ActiveSourceTabId);
        model.SelectedEntry = model.Entries.Single(e => e.Path == "b.cs");
        await model.ToggleBlameAsync(); Assert.Null(model.CurrentDocument!.SourceRevision); Assert.Equal("other\n", model.CurrentDocument.Text);
        Assert.Equal(4, model.SourceTabs.Count);
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Removing_working_file_during_refresh_keeps_immutable_historical_tab()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "one\n"); repo.Write("b.cs", "other\n"); repo.Commit("first");
        repo.Write("a.cs", "two\n"); repo.Commit("second");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SelectedEntry = model.Entries.Single(e => e.Path == "a.cs");
        await model.ToggleBlameAsync(); await model.PreviousAnnotationAsync(1, model.DocumentVersion);
        repo.Git("rm", "a.cs"); repo.Commit("remove"); await model.RefreshAsync();
        Assert.NotNull(model.CurrentDocument!.SourceRevision); Assert.Equal("one\n", model.CurrentDocument.Text); Assert.Equal(2, model.SourceTabs.Count);
    }

    [Fact]
    public async Task Field_preferences_merge_deltas_restore_and_reject_an_empty_gutter()
    {
        using var repo = new TestRepository(); var directory = Path.Combine(repo.Root, "settings");
        var store = new BlameViewStore(directory); Assert.Equal(new BlameViewSettings(), await store.ReadAsync());
        await Task.WhenAll(store.UpdateAsync("date", true), store.UpdateAsync("commitNumber", true), store.UpdateAsync("revision", false));
        var restored = await new BlameViewStore(directory).ReadAsync(); Assert.Equal(new(false, true, true, true), restored);
        await store.UpdateAsync("author", false); await store.UpdateAsync("date", false);
        await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync("commitNumber", false));
        Assert.True((await store.ReadAsync()).CommitNumber);
        await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync("unknown", true));
        File.WriteAllText(Path.Combine(directory, "blame-view.json"), "{ broken json");
        Assert.Equal(new BlameViewSettings(), await store.ReadAsync());
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        for (var i = 0; i < 100 && !ready(); i++) await Task.Delay(20);
        Assert.True(ready());
    }
}
