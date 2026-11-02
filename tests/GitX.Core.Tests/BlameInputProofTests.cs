using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class BlameInputProofTests
{
    [Fact]
    public void Configuration_origins_preserve_values_and_accept_relative_repository_paths()
    {
        using var repo = new TestRepository();
        var entries = BlameInputProof.ParseConfiguration("file:.git/config\0core.Test\nfirst\nsecond\0command line:\0core.quotepath\nfalse\0");
        Assert.NotNull(entries); Assert.Equal("first\nsecond", entries[0].Value);
        var proof = new BlameInputProof([Path.Combine(repo.Root, ".git", "config")], "digest");
        Assert.True(proof.Covers(entries, repo.Root));
        Assert.False(proof.Covers([new("file:../other", "core.test", "")], repo.Root));
        var valueless = BlameInputProof.ParseConfiguration("file:.git/config\0test.enabled\0file:.git/config\0test.empty\n\0");
        Assert.Equal(2, valueless!.Length); Assert.All(valueless, entry => Assert.Equal("", entry.Value));
    }

    [Theory]
    [InlineData("file:.git/config\0")]
    [InlineData("file:.git/config\0missing-value-separator\0")]
    [InlineData("file:.git/config\0\nvalue\0")]
    public void Malformed_configuration_disables_the_proof(string output) => Assert.Null(BlameInputProof.ParseConfiguration(output));

    [Theory]
    [InlineData("file:.git/config", "include.path", "included")]
    [InlineData("file:.git/config", "includeif.gitdir:x.path", "included")]
    [InlineData("file:.git/config", "core.attributesfile", "custom")]
    [InlineData("command line:", "core.quotepath", "true")]
    [InlineData("command line:", "core.abbrev", "7")]
    [InlineData("standard input:", "core.test", "value")]
    public void Uncovered_configuration_sources_require_the_Git_recheck(string origin, string name, string value)
    {
        using var repo = new TestRepository();
        var proof = new BlameInputProof([Path.Combine(repo.Root, ".git", "config")], "digest");
        Assert.False(proof.Covers([new(origin, name, value)], repo.Root));
    }

    [Fact]
    public void Defaults_use_the_complete_final_block_including_both_possible_global_configs()
    {
        using var repo = new TestRepository(); var prefix = Path.GetFullPath(repo.Container).Replace('\\', '/');
        var block = $"GIT_ATTR_SYSTEM={prefix}/system-attr\nGIT_ATTR_GLOBAL={prefix}/global-attr\nGIT_CONFIG_SYSTEM={prefix}/system-config\nGIT_CONFIG_GLOBAL={prefix}/xdg-config\nGIT_CONFIG_GLOBAL={prefix}/home-config\n";
        var result = BlameInputProof.ParseDefaultFiles("core.test=multi\nGIT_ATTR_SYSTEM=fake\ntext\n" + block);
        Assert.Equal(5, result!.Length);
        Assert.Contains(Path.Combine(repo.Container, "home-config"), result);
        Assert.Contains(Path.Combine(repo.Container, "xdg-config"), result);
        Assert.Null(BlameInputProof.ParseDefaultFiles(block + "unexpected=future-format\n"));
        Assert.Null(BlameInputProof.ParseDefaultFiles(block.Replace("GIT_ATTR_GLOBAL=", "GIT_UNKNOWN=")));
        Assert.Null(BlameInputProof.ParseDefaultFiles(block.Replace(prefix + "/global-attr", "relative/attr")));
        Assert.Null(BlameInputProof.ParseDefaultFiles(block.Replace("/system-attr\n", "/system-attr\r\n")));
        Assert.Null(BlameInputProof.ParseDefaultFiles(block.Replace($"GIT_CONFIG_SYSTEM={prefix}/system-config\n", "")));
    }

    [Fact]
    public async Task Full_bytes_detect_same_size_same_timestamp_creation_deletion_and_file_kind_changes()
    {
        using var repo = new TestRepository(); var file = Path.Combine(repo.Container, "input");
        var missing = new BlameInputProof([file], BlameInputProof.HashFiles([file], default)!);
        Assert.True(await missing.UnchangedAsync(default)); File.WriteAllText(file, "one"); Assert.False(await missing.UnchangedAsync(default));
        var present = new BlameInputProof([file], BlameInputProof.HashFiles([file], default)!); var time = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "two"); File.SetLastWriteTimeUtc(file, time); Assert.False(await present.UnchangedAsync(default));
        File.Delete(file); Assert.True(await missing.UnchangedAsync(default)); Assert.False(await present.UnchangedAsync(default));
        Directory.CreateDirectory(file); Assert.Null(BlameInputProof.HashFiles([file], default));
    }

    [Fact]
    public async Task Source_proof_covers_missing_ancestor_attributes_and_repository_overrides()
    {
        using var repo = new TestRepository(); repo.Write("nested/deep/a.txt", "one\n"); repo.Commit("initial");
        foreach (var relative in new[] { ".gitattributes", "nested/.gitattributes", "nested/deep/.gitattributes", ".git/config.worktree", ".git/info/attributes", ".git/commondir" })
        {
            var proof = await BlameInputProof.CaptureAsync(repo.Root, "nested/deep/a.txt", default);
            Assert.NotNull(proof); Assert.Contains(Path.Combine(repo.Root, relative.Replace('/', Path.DirectorySeparatorChar)), proof.Files);
            Assert.True(await proof.UnchangedAsync(default)); repo.Write(relative, "new input\n"); Assert.False(await proof.UnchangedAsync(default));
            File.Delete(Path.Combine(repo.Root, relative));
        }
        var before = await BlameInputProof.CaptureAsync(repo.Root, "nested/deep/a.txt", default);
        repo.Git("config", "core.abbrev", "8"); Assert.False(await before!.UnchangedAsync(default));
    }

    [Fact]
    public void File_read_budgets_and_cancellation_do_not_return_a_partial_proof()
    {
        using var repo = new TestRepository(); var file = Path.Combine(repo.Container, "large");
        File.WriteAllBytes(file, new byte[2 * 1024 * 1024 + 1]); Assert.Null(BlameInputProof.HashFiles([file], default));
        var files = Enumerable.Range(0, 5).Select(i => file + i).ToArray();
        foreach (var input in files) File.WriteAllBytes(input, new byte[2 * 1024 * 1024]);
        Assert.Null(BlameInputProof.HashFiles(files, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BlameInputProof.HashFiles([file], cancelled.Token));
    }

    [Fact]
    public async Task Linked_worktree_sources_include_private_and_common_metadata()
    {
        using var repo = new TestRepository(); repo.Write("a", "one\n"); repo.Commit("initial");
        var worktree = Path.Combine(repo.Container, "linked"); repo.Git("worktree", "add", "-b", "linked", worktree);
        var proof = await BlameInputProof.CaptureAsync(worktree, "a", default); Assert.NotNull(proof);
        Assert.Contains(Path.Combine(repo.Root, ".git", "config"), proof.Files);
        Assert.Contains(Path.Combine(repo.Root, ".git", "info", "attributes"), proof.Files);
        var privateDirectory = Path.Combine(repo.Root, ".git", "worktrees", "linked");
        Assert.Contains(Path.Combine(privateDirectory, "config.worktree"), proof.Files);
        File.WriteAllText(Path.Combine(privateDirectory, "config.worktree"), "[core]\n abbrev = 8\n");
        Assert.False(await proof.UnchangedAsync(default));
    }

    [Fact]
    public async Task Included_configs_retain_the_full_guard_even_when_the_include_does_not_exist()
    {
        using var repo = new TestRepository(); repo.Write("a", "one\n"); repo.Commit("initial");
        repo.Git("config", "include.path", Path.Combine(repo.Container, "missing.config"));
        var input = await BlameCache.ReadInputAsync(repo.Root, "a", repo.Git("rev-parse", "HEAD").Trim(), "state", true, default, captureProof: true);
        Assert.NotNull(input.Key); Assert.Null(input.Proof);
    }
}
