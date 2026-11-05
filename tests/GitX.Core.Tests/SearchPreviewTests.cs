using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class SearchPreviewTests
{
    [Fact]
    public async Task Reads_disk_not_index_and_never_creates_editor_tabs_or_writes()
    {
        using var repo = new TestRepository(); repo.Write("src/中文.cs", "staged\r\n"); repo.Commit("base");
        repo.Write("src/中文.cs", "disk 😀\r\ntarget\r\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        var before = RepositoryState.Token(repo.Root, ["src/中文.cs"]);
        var count = model.SourceTabs.Count;
        var preview = await RepositoryReader.SearchPreviewAsync(repo.Root, new("src/中文.cs", 2, 1, 6, "target"));
        Assert.Contains("disk 😀", preview.Text); Assert.Contains("›     2  target", preview.Text);
        Assert.DoesNotContain("staged", preview.Text); Assert.Contains("磁盘", preview.Source); Assert.Contains("不含未保存编辑", preview.Source);
        Assert.Equal(WorkingTextFiles.RootKey(repo.Root), preview.RootKey);
        Assert.Equal(before, RepositoryState.Token(repo.Root, ["src/中文.cs"])); Assert.Equal(count, model.SourceTabs.Count);
    }

    [Fact]
    public async Task Bounds_rendered_context_including_long_selected_line()
    {
        using var repo = new TestRepository();
        repo.Write("a.txt", string.Join('\n', Enumerable.Range(1, 1000).Select(i => i == 500 ? new string('a', 15000) + "NEEDLE" : "line " + i)));
        var preview = await RepositoryReader.SearchPreviewAsync(repo.Root, new("a.txt", 500, 15001, 6, "NEEDLE"));
        Assert.True(preview.Truncated); Assert.Equal(460, preview.FirstLine); Assert.Equal(1000, preview.TotalLines);
        Assert.Equal(RepositoryReader.MaxSearchPreviewLines, preview.Text.Split('\n').Length);
        Assert.True(preview.Text.Length <= RepositoryReader.MaxSearchPreviewCharacters);
        Assert.Contains("NEEDLE", preview.Text); Assert.DoesNotContain("line 1\n", preview.Text);
    }

    [Fact]
    public async Task Changed_text_match_is_rejected_and_file_result_can_read_fresh_content()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "needle\n");
        var result = Assert.Single((await RepositoryReader.SearchAsync(repo.Root, ["a.txt"], new("needle"))).Matches);
        Assert.Contains("needle", (await RepositoryReader.SearchPreviewAsync(repo.Root, result)).Text);
        repo.Write("a.txt", "changed\n");
        var error = await Assert.ThrowsAsync<IOException>(() => RepositoryReader.SearchPreviewAsync(repo.Root, result));
        Assert.Contains("内容已变化", error.Message);
        Assert.Contains("changed", (await RepositoryReader.SearchPreviewAsync(repo.Root, result with { Fingerprint = null })).Text);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(".git/config")]
    [InlineData("sub/.GIT/config")]
    [InlineData("/absolute")]
    [InlineData("a.txt:stream")]
    [InlineData("sub/../a.txt")]
    public async Task Rejects_unsafe_paths(string path)
    {
        using var repo = new TestRepository();
        await Assert.ThrowsAsync<IOException>(() => RepositoryReader.SearchPreviewAsync(repo.Root, new(path, 1, 1, 0, "")));
    }

    [Fact]
    public async Task Rejects_binary_encoding_size_long_line_missing_and_invalid_line()
    {
        using var repo = new TestRepository();
        File.WriteAllBytes(Path.Combine(repo.Root, "binary"), [0, 1, 2]);
        File.WriteAllBytes(Path.Combine(repo.Root, "invalid"), [255, 254, 255]);
        repo.Write("large", new string('x', RepositoryReader.MaxTextBytes + 1));
        repo.Write("long", new string('x', 20001)); repo.Write("small", "text");
        foreach (var path in new[] { "binary", "invalid", "large", "long", "missing" })
            await Assert.ThrowsAnyAsync<IOException>(() => RepositoryReader.SearchPreviewAsync(repo.Root, new(path, 1, 1, 0, "")));
        await Assert.ThrowsAsync<IOException>(() => RepositoryReader.SearchPreviewAsync(repo.Root, new("small", 2, 1, 0, "")));
        await Assert.ThrowsAsync<IOException>(() => RepositoryReader.SearchPreviewAsync(repo.Root, new("small", 1, 0, 0, "")));
    }

    [Fact]
    public async Task Rejects_canceled_read_and_links()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "hello");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepositoryReader.SearchPreviewAsync(repo.Root, new("a.txt", 1, 1, 0, ""), cancellation.Token));
        if (OperatingSystem.IsWindows()) return; // Windows link creation requires platform-specific privilege.
        File.CreateSymbolicLink(Path.Combine(repo.Root, "link"), Path.Combine(repo.Root, "a.txt"));
        await Assert.ThrowsAsync<IOException>(() => RepositoryReader.SearchPreviewAsync(repo.Root, new("link", 1, 1, 0, "")));
    }

    [Fact]
    public async Task Same_path_in_two_repositories_keeps_source_identity_and_empty_files_work()
    {
        using var first = new TestRepository(); using var second = new TestRepository();
        first.Write("a.txt", "first"); second.Write("a.txt", "second");
        var match = new CodeSearchMatch("a.txt", 1, 1, 0, "");
        var a = await RepositoryReader.SearchPreviewAsync(first.Root, match);
        var b = await RepositoryReader.SearchPreviewAsync(second.Root, match);
        Assert.NotEqual(a.RootKey, b.RootKey); Assert.Contains("first", a.Text); Assert.Contains("second", b.Text);
        first.Write("empty.txt", "");
        var empty = await RepositoryReader.SearchPreviewAsync(first.Root, match with { Path = "empty.txt" });
        Assert.Equal(1, empty.TotalLines); Assert.False(empty.Truncated);
    }

    [Fact]
    public async Task Old_failure_cannot_replace_current_preview_or_error_state()
    {
        var root = Path.GetTempPath(); var old = new TaskCompletionSource<CodeSearchPreview>();
        using var session = new SearchPreviewSession((_, match, _) => match.Path == "old" ? old.Task : Task.FromResult(Preview(root, "new")));
        var pending = session.LoadAsync(root, new("old", 1, 1, 0, ""), () => true);
        Assert.True(await session.LoadAsync(root, new("new", 1, 1, 0, ""), () => true));
        old.SetException(new IOException("stale failure")); Assert.False(await pending);
        Assert.Equal("new", session.Current!.Path); Assert.Null(session.Error);
    }

    [Fact]
    public async Task Rapid_100_selections_only_publish_current_result_and_release_on_hide()
    {
        var reads = new List<TaskCompletionSource<CodeSearchPreview>>(); var tokens = new List<CancellationToken>();
        using var session = new SearchPreviewSession((_, _, token) =>
        {
            tokens.Add(token); var read = new TaskCompletionSource<CodeSearchPreview>(); reads.Add(read); return read.Task;
        });
        var root = Path.GetTempPath(); var loads = new List<Task<bool>>();
        for (var i = 0; i < 100; i++) loads.Add(session.LoadAsync(root, new($"{i}.txt", 1, 1, 0, ""), () => true));
        Assert.All(tokens.Take(99), token => Assert.True(token.IsCancellationRequested));
        reads[99].SetResult(Preview(root, "99.txt")); Assert.True(await loads[99]);
        for (var i = 98; i >= 0; i--) { reads[i].SetResult(Preview(root, $"{i}.txt")); Assert.False(await loads[i]); }
        Assert.Equal("99.txt", session.Current!.Path); Assert.False(session.IsLoading);
        session.Invalidate(); Assert.Null(session.Current); Assert.Null(session.Error);
    }

    [Fact]
    public async Task Hide_close_and_context_change_reject_late_content()
    {
        foreach (var reason in new[] { "hide", "close", "context" })
        {
            var read = new TaskCompletionSource<CodeSearchPreview>(); var current = true;
            using var session = new SearchPreviewSession((_, _, _) => read.Task);
            var root = Path.GetTempPath(); var task = session.LoadAsync(root, new("a", 1, 1, 0, ""), () => current);
            if (reason == "hide") session.Invalidate(); else if (reason == "close") session.Dispose(); else current = false;
            read.SetResult(Preview(root, "a")); await task;
            Assert.Null(session.Current); Assert.False(session.IsLoading);
            if (reason == "context") Assert.Contains("仓库或文档", session.Error);
        }
    }

    [Fact]
    public async Task Failed_read_can_retry_but_mismatched_identity_cannot_publish()
    {
        var root = Path.GetTempPath(); var attempt = 0;
        using var session = new SearchPreviewSession((_, _, _) => ++attempt == 1
            ? Task.FromException<CodeSearchPreview>(new IOException("unreadable")) : Task.FromResult(Preview(root, "a")));
        Assert.True(await session.LoadAsync(root, new("a", 1, 1, 0, ""), () => true));
        Assert.Equal("unreadable", session.Error); Assert.Null(session.Current);
        Assert.True(await session.LoadAsync(root, new("a", 1, 1, 0, ""), () => true));
        Assert.NotNull(session.Current); Assert.Null(session.Error);
        Assert.True(await session.LoadAsync(root, new("different", 1, 1, 0, ""), () => true));
        Assert.Null(session.Current); Assert.Contains("不符", session.Error);
    }

    private static CodeSearchPreview Preview(string root, string path) => new(WorkingTextFiles.RootKey(root), path, "hello", 1, 1, 1, false);
}
