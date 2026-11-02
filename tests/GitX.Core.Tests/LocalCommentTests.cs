using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class LocalCommentTests
{
    private readonly RepositoryReader _reader = new();
    [Fact]
    public async Task Anchors_capture_source_side_range_crlf_and_comparison_not_display_rows()
    {
        using var repo = new TestRepository();
        repo.Write("文件.cs", "one\r\ntwo\r\nthree\r\n"); repo.Commit("base");
        repo.Write("文件.cs", "one\r\nNEW\r\nextra\r\nthree");
        var doc = await _reader.DocumentAsync(new(repo.Root, "文件.cs", ComparisonKind.Worktree));
        var old = CommentAnchor.Capture(doc, 0, 2, 3); var current = CommentAnchor.Capture(doc, 1, 2, 3);
        Assert.Equal("two\nthree", old.Code); Assert.Equal("NEW\nextra", current.Code);
        Assert.Equal(0, old.Side); Assert.Equal(1, current.Side);
        Assert.Equal(ComparisonKind.Worktree, old.Context.Kind);
        Assert.Throws<IOException>(() => CommentAnchor.Capture(doc, 1, 5, 5));
        var same = await _reader.DocumentAsync(new(repo.Root, "文件.cs", ComparisonKind.Worktree));
        Assert.True(old.Matches(same.CommentContext));
        repo.Write("文件.cs", "insert\r\none\r\nNEW\r\nextra\r\nthree");
        var changed = await _reader.DocumentAsync(new(repo.Root, "文件.cs", ComparisonKind.Worktree));
        Assert.False(old.Matches(changed.CommentContext));
        Assert.Equal("two\nthree", old.Code);
    }
    [Fact]
    public async Task Snapshot_separates_browse_index_worktree_history_and_head_changes()
    {
        using var repo = new TestRepository(); repo.Write("a", "base\n"); repo.Commit("base");
        var first = repo.Git("rev-parse", "HEAD").Trim();
        var contexts = new List<CommentContext>();
        foreach (var kind in new[] { ComparisonKind.WorkingFile, ComparisonKind.Worktree, ComparisonKind.Index, ComparisonKind.Commit })
            contexts.Add((await _reader.DocumentAsync(new(repo.Root, "a", kind, first))).CommentContext!);
        Assert.Equal(4, contexts.Select(c => c.Snapshot).Distinct().Count());
        repo.Write("unrelated", "new"); repo.Commit("change HEAD without changing a");
        Assert.NotEqual(contexts[0], (await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.WorkingFile))).CommentContext);
        Assert.Equal(contexts[3], (await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.Commit, first))).CommentContext);
    }
    [Fact]
    public async Task Empty_missing_binary_and_conflict_sources_reject_line_comments()
    {
        using var repo = new TestRepository(); repo.Write("a", "base"); repo.Commit("base");
        File.Delete(Path.Combine(repo.Root, "a"));
        var deleted = await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.Worktree));
        Assert.Equal("base", CommentAnchor.Capture(deleted, 0, 1, 1).Code);
        Assert.Throws<IOException>(() => CommentAnchor.Capture(deleted, 1, 1, 1));
        repo.Write("a", ""); var empty = await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.WorkingFile));
        Assert.Throws<IOException>(() => CommentAnchor.Capture(empty, 1, 1, 1));
        repo.Write("a", "\0binary"); var binary = await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.WorkingFile));
        Assert.Null(binary.CommentContext);
        repo.Write("a", "text"); var conflict = await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.Conflict));
        Assert.Null(conflict.CommentContext);
    }
    [Fact]
    public async Task Persistence_restores_unicode_and_keeps_worktrees_and_repositories_isolated()
    {
        using var repo = new TestRepository(); using var other = new TestRepository();
        repo.Write("a", "你好"); repo.Commit("base"); other.Write("a", "你好"); other.Commit("base");
        var linked = Path.Combine(repo.Container, "linked"); repo.Git("worktree", "add", "-b", "linked", linked);
        var a = CommentAnchor.Capture(await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.WorkingFile)), 1, 1, 1);
        var b = CommentAnchor.Capture(await _reader.DocumentAsync(new(other.Root, "a", ComparisonKind.WorkingFile)), 1, 1, 1);
        var c = CommentAnchor.Capture(await _reader.DocumentAsync(new(linked, "a", ComparisonKind.WorkingFile)), 1, 1, 1);
        var directory = Path.Combine(repo.Container, "comments"); var store = new LocalCommentStore(directory);
        var saved = await store.SaveAsync(a, "两次输入中文\n不丢字 😀");
        Assert.Equal(saved, Assert.Single(await new LocalCommentStore(directory).ReadAsync(a.Context.RepositoryId)));
        Assert.Empty(await store.ReadAsync(b.Context.RepositoryId)); Assert.Empty(await store.ReadAsync(c.Context.RepositoryId));
        Assert.False(a.Matches(b.Context)); Assert.False(a.Matches(c.Context));
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }
    [Fact]
    public async Task Concurrent_saves_do_not_drop_notes_and_stale_edits_or_deletes_are_rejected()
    {
        using var repo = new TestRepository(); repo.Write("a", "line");
        var anchor = CommentAnchor.Capture(await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.WorkingFile)), 1, 1, 1);
        var directory = Path.Combine(repo.Container, "comments");
        var stores = new[] { new LocalCommentStore(directory), new LocalCommentStore(directory) };
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => stores[i % 2].SaveAsync(anchor, "note " + i)));
        var all = await stores[0].ReadAsync(anchor.Context.RepositoryId); Assert.Equal(12, all.Count);
        var original = all[0]; var edited = await stores[0].SaveAsync(anchor, "edited", original);
        await Assert.ThrowsAsync<IOException>(() => stores[1].SaveAsync(anchor, "stale", original));
        await Assert.ThrowsAsync<IOException>(() => stores[1].DeleteAsync(original));
        await stores[0].DeleteAsync(edited); Assert.Equal(11, (await stores[0].ReadAsync(anchor.Context.RepositoryId)).Count);
    }
    [Fact]
    public async Task Corrupt_or_unknown_storage_is_not_overwritten_and_limits_are_explicit()
    {
        using var repo = new TestRepository(); repo.Write("a", "line");
        var anchor = CommentAnchor.Capture(await _reader.DocumentAsync(new(repo.Root, "a", ComparisonKind.WorkingFile)), 1, 1, 1);
        var directory = Path.Combine(repo.Container, "comments"); Directory.CreateDirectory(directory);
        var store = new LocalCommentStore(directory); var file = Path.Combine(directory, anchor.Context.RepositoryId + ".json");
        foreach (var content in new[] { "{ broken", "{\"Format\":999}" })
        {
            File.WriteAllText(file, content);
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(anchor, "no overwrite"));
            Assert.Equal(content, File.ReadAllText(file));
        }
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(anchor, " "));
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(anchor, new string('x', 16001)));
    }
    [Fact]
    public async Task Export_is_deterministic_and_preserves_original_version_code_and_literal_notes()
    {
        using var repo = new TestRepository(); repo.Write("中文.md", "```cs\n你好\n```\n");
        var anchor = CommentAnchor.Capture(await _reader.DocumentAsync(new(repo.Root, "中文.md", ComparisonKind.WorkingFile)), 1, 1, 3);
        var store = new LocalCommentStore(Path.Combine(repo.Container, "comments"));
        var saved = await store.SaveAsync(anchor, "````\n<script>这是文本</script>\n换行");
        repo.Write("中文.md", "unrelated");
        var markdown = CommentMarkdown.Export([saved]);
        Assert.Contains("中文.md</code> · 新侧 · L1–3", markdown);
        Assert.Contains("````markdown\n```cs\n你好\n```\n````", markdown);
        Assert.Contains("`````text\n````\n<script>这是文本</script>\n换行\n`````", markdown);
        Assert.Contains(anchor.Context.Snapshot, markdown); Assert.Contains("unborn", markdown);
        Assert.DoesNotContain("unrelated", markdown);
        Assert.Equal(markdown, CommentMarkdown.Export(await store.ReadAsync(anchor.Context.RepositoryId)));
    }
}
