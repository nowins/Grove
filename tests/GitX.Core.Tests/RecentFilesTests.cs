using System.Text.Json;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RecentFilesTests
{
    [Fact]
    public async Task Persists_mru_without_duplicates_and_separates_worktrees_and_revisions()
    {
        var profile = Path.Combine(Path.GetTempPath(), "grove-recent-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Path.Combine(profile, "repo"); var b = Path.Combine(profile, "linked"); var sha = new string('a', 40);
            var store = new RecentFileStore(profile);
            await store.RememberAsync(new(a, "目录/one.py")); await store.RememberAsync(new(a, "two.ts"));
            await store.RememberAsync(new(a + Path.DirectorySeparatorChar, "目录/one.py"));
            await store.RememberAsync(new(b, "private.js")); await store.RememberAsync(new(a, "目录/one.py", sha));
            var restored = await new RecentFileStore(profile).ReadAsync(a);
            Assert.Equal(new[] { ("目录/one.py", sha), ("目录/one.py", (string?)null), ("two.ts", null) }, restored.Select(f => (f.Path, f.Revision)));
            Assert.Equal("private.js", Assert.Single(await store.ReadAsync(b)).Path);
            await store.ForgetAsync(restored[0]); Assert.Equal(2, (await store.ReadAsync(a)).Count);
            Assert.False(Directory.Exists(a)); // MRU never creates/scans target repositories.
        }
        finally { if (Directory.Exists(profile)) Directory.Delete(profile, true); }
    }

    [Fact]
    public async Task Concurrent_writes_are_atomic_bounded_and_corrupt_records_are_ignored()
    {
        var profile = Path.Combine(Path.GetTempPath(), "grove-recent-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(profile); var root = Path.Combine(profile, "repo"); var path = Path.Combine(profile, "recent-files.json");
            await File.WriteAllTextAsync(path, "[broken"); var store = new RecentFileStore(profile); Assert.Empty(await store.ReadAsync(root));
            await Task.WhenAll(Enumerable.Range(0, 80).Select(i => store.RememberAsync(new(root, i + ".js"))));
            Assert.Equal(64, (await store.ReadAsync(root)).Count); Assert.Single(Directory.GetFiles(profile));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new RecentFile?[] { null, new(root, "../outside"), new(root + "\0", "x.py"), new(root, "valid.ts") }));
            Assert.Equal("valid.ts", Assert.Single(await store.ReadAsync(root)).Path);
            for (var i = 0; i < 35; i++) await store.RememberAsync(new(Path.Combine(profile, "r" + i), "file.py"));
            var all = JsonSerializer.Deserialize<RecentFile[]>(await File.ReadAllTextAsync(path))!;
            Assert.Equal(32, all.Select(f => f.Root).Distinct().Count());
        }
        finally { if (Directory.Exists(profile)) Directory.Delete(profile, true); }
    }

    [Theory]
    [InlineData("../outside")][InlineData("/absolute")][InlineData(".git/config")][InlineData("a\\b")][InlineData("a/../b")][InlineData("a//b")][InlineData("C:bad")]
    public void Unsafe_persistent_paths_are_rejected(string path) => Assert.False(RecentFileStore.SafePath(path));

    [Fact]
    public void Search_preserves_recency_and_supports_directory_filename_and_revision()
    {
        var files = new[] { new RecentFile("repo", "src/界面.ts"), new RecentFile("repo", "tests/界面.ts", new string('a', 40)), new RecentFile("repo", "src/main.py") };
        Assert.Equal(files.Take(2), RecentFileStore.Filter(files, "界面"));
        Assert.Equal(new[] { files[0] }, RecentFileStore.Filter(files, "src 界面"));
        Assert.Equal(new[] { files[1] }, RecentFileStore.Filter(files, "aaaa"));
        Assert.Empty(RecentFileStore.Filter(files, "missing"));
    }

    [Fact]
    public async Task Missing_file_and_wrong_repository_leave_current_document_and_tabs_intact()
    {
        using var repo = new TestRepository(); using var other = new TestRepository();
        repo.Write("first.py", "print(1)\n"); repo.Write("second.ts", "const second = 2;\n"); repo.Commit("fixture");
        var sha = repo.Git("rev-parse", "HEAD").Trim(); using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        Assert.True(await model.OpenRecentFileAsync(repo.Root, "first.py", null)); var first = model.ActiveSourceTabId;
        Assert.True(await model.OpenRecentFileAsync(repo.Root, "second.ts", null));
        Assert.True(await model.OpenRecentFileAsync(repo.Root, "first.py", null)); Assert.Equal(first, model.ActiveSourceTabId); Assert.Equal(2, model.SourceTabs.Count);
        var document = model.CurrentDocument; var version = model.DocumentVersion;
        Assert.False(await model.OpenRecentFileAsync(other.Root, "first.py", null));
        Assert.False(await model.OpenRecentFileAsync(repo.Root, "missing.py", null));
        Assert.False(await model.OpenRecentFileAsync(repo.Root, "../outside", null));
        Assert.Same(document, model.CurrentDocument); Assert.Equal(version, model.DocumentVersion); Assert.Equal(first, model.ActiveSourceTabId);
        repo.Git("rm", "first.py"); repo.Commit("delete source");
        Assert.False(await model.OpenRecentFileAsync(repo.Root, "first.py", null)); Assert.Same(document, model.CurrentDocument);
        Assert.True(await model.OpenRecentFileAsync(repo.Root, "first.py", sha)); Assert.Equal(sha, model.CurrentDocument!.SourceRevision);
        await model.OpenAsync(other.Root); Assert.Empty(model.SourceTabs);
        Assert.False(await model.OpenRecentFileAsync(repo.Root, "first.py", sha));
    }

    [Fact]
    public void Both_menus_cover_every_command_once_with_platform_gestures()
    {
        Assert.Equal(Enum.GetValues<ShellCommandId>().Order(), ShellCommands.All.Select(c => c.Id).Order());
        var mac = ShellCommands.WindowMenu(true).Select(c => c.Id).ToHashSet();
        Assert.DoesNotContain(ShellCommandId.About, mac); Assert.DoesNotContain(ShellCommandId.Updates, mac);
        Assert.Contains(ShellCommandId.OpenRepository, mac); Assert.Contains(ShellCommandId.RecentFiles, mac);
        Assert.Equal("Ctrl+E", ShellCommands.All.Single(c => c.Id == ShellCommandId.RecentFiles).Gesture(false));
        Assert.Equal("Meta+E", ShellCommands.All.Single(c => c.Id == ShellCommandId.RecentFiles).Gesture(true));
        foreach (var platform in new[] { false, true })
        {
            var gestures = ShellCommands.All.Select(c => c.Gesture(platform)).OfType<string>().ToArray();
            Assert.Equal(gestures.Length, gestures.Distinct().Count());
        }
    }
}
