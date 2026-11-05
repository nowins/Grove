using System.Text.Json;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;
public sealed class TextDraftStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "grove-drafts-test-" + Guid.NewGuid().ToString("N"));
    private TextDraftStore Store(bool enabled = true) { var store = new TextDraftStore(directory); store.SetEnabled(enabled); return store; }
    private static async Task<WorkingTextEditInfo> Baseline(TestRepository repo, string path = "text.txt")
    { repo.Write(path, "base\n"); repo.Commit("base"); return (await new RepositoryReader().DocumentAsync(new(repo.Root, path, ComparisonKind.WorkingFile))).Editing!; }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact] public async Task Disabled_by_default_and_disabling_blocks_new_writes_but_keeps_existing_copy()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = new TextDraftStore(directory);
        Assert.False(store.Enabled); Assert.Throws<IOException>(() => store.Put(repo.Root, "text.txt", info, "draft", null));
        store.SetEnabled(true); var saved = store.Put(repo.Root, "text.txt", info, "draft", null);
        store.SetEnabled(false); Assert.Throws<IOException>(() => store.Put(repo.Root, "text.txt", info, "new", saved.Revision));
        Assert.Equal("draft", Assert.Single(store.List(repo.Root).Drafts).Text);
    }
    [Fact] public async Task Restart_roundtrip_never_changes_worktree_index_or_head()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var sha = repo.Git("rev-parse", "HEAD");
        var saved = Store().Put(repo.Root, "text.txt", info, "私有 draft\n", null);
        Assert.Equal(saved, Assert.Single(new TextDraftStore(directory).List(repo.Root).Drafts));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(repo.Root, "text.txt")));
        Assert.Equal(sha, repo.Git("rev-parse", "HEAD")); Assert.Equal("", repo.Git("status", "--porcelain"));
    }
    [Fact] public async Task Interrupted_install_preserves_previous_complete_checkpoint()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store(); var first = store.Put(repo.Root, "text.txt", info, "first", null);
        var failing = new TextDraftStore(directory, () => DateTimeOffset.UtcNow, () => throw new IOException("injected before atomic install"));
        Assert.Throws<IOException>(() => failing.Put(repo.Root, "text.txt", info, "second", first.Revision));
        Assert.Equal(first, Assert.Single(store.List(repo.Root).Drafts)); Assert.Empty(Directory.GetFiles(directory, "*.pending"));
    }
    [Fact] public async Task Concurrent_stale_write_and_discard_cannot_replace_or_delete_newer_draft()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store(); var one = store.Put(repo.Root, "text.txt", info, "one", null);
        var two = store.Put(repo.Root, "text.txt", info, "two", one.Revision);
        Assert.Throws<IOException>(() => store.Put(repo.Root, "text.txt", info, "stale", one.Revision));
        Assert.Throws<IOException>(() => store.Put(repo.Root, "text.txt", info, "unreviewed", null));
        Assert.Throws<IOException>(() => store.Discard(repo.Root, one)); Assert.Equal(two, Assert.Single(store.List(repo.Root).Drafts));
        store.Discard(repo.Root, two); Assert.Empty(store.List(repo.Root).Drafts);
    }
    [Fact] public async Task Same_relative_name_in_different_repositories_is_isolated()
    {
        using var a = new TestRepository(); using var b = new TestRepository(); var ai = await Baseline(a); var bi = await Baseline(b); var store = Store();
        store.Put(a.Root, "text.txt", ai, "A", null); store.Put(b.Root, "text.txt", bi, "B", null);
        Assert.Equal("A", Assert.Single(store.List(a.Root).Drafts).Text); Assert.Equal("B", Assert.Single(store.List(b.Root).Drafts).Text);
        Assert.Throws<IOException>(() => store.Put(b.Root, "other.txt", ai, "wrong root", null));
    }
    [Fact] public async Task External_disk_change_and_deletion_do_not_alter_baseline_or_recreate_target()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store(); var saved = store.Put(repo.Root, "text.txt", info, "mine", null);
        repo.Write("text.txt", "external");
        await Assert.ThrowsAsync<IOException>(() => new WorkingTextWriter().SaveAsync(repo.Root, saved.Path, saved.Baseline.Fingerprint, saved.Text));
        Assert.Equal("external", File.ReadAllText(Path.Combine(repo.Root, "text.txt")));
        File.Delete(Path.Combine(repo.Root, "text.txt")); Assert.Single(store.List(repo.Root).Drafts);
        await Assert.ThrowsAnyAsync<IOException>(() => new WorkingTextWriter().SaveAsync(repo.Root, saved.Path, saved.Baseline.Fingerprint, saved.Text));
        Assert.False(File.Exists(Path.Combine(repo.Root, "text.txt")));
    }
    [Theory]
    [InlineData(".env")][InlineData("config/.env.production")][InlineData("x/secret.pem")][InlineData(".ssh/config")][InlineData("credentials.json")][InlineData("id_ed25519")]
    public async Task Sensitive_paths_are_not_persisted(string path)
    { using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store(); Assert.Throws<IOException>(() => store.Put(repo.Root, path, info, "secret", null)); Assert.Empty(store.List(repo.Root).Drafts); }
    [Theory][InlineData("../escape")][InlineData("/tmp/escape")][InlineData("a/../escape")][InlineData("a\\b")][InlineData("a//b")]
    public async Task Unsafe_paths_are_rejected(string path)
    { using var repo = new TestRepository(); var info = await Baseline(repo); Assert.Throws<IOException>(() => Store().Put(repo.Root, path, info, "draft", null)); }
    [Fact] public async Task Oversize_and_quota_failure_preserve_earlier_data_without_eviction()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store();
        Assert.Throws<IOException>(() => store.Put(repo.Root, "text.txt", info, new string('x', TextDraftStore.MaxTextBytes + 1), null));
        for (int i = 0; i < TextDraftStore.MaxRecords; i++) store.Put(repo.Root, $"file{i}.txt", info, "small", null);
        Assert.Throws<IOException>(() => store.Put(repo.Root, "overflow.txt", info, "more", null)); Assert.Equal(TextDraftStore.MaxRecords, store.List(repo.Root).Drafts.Count);
    }
    [Fact] public async Task Corrupt_record_does_not_hide_valid_drafts_and_orphan_pending_is_not_recovered()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store(); store.Put(repo.Root, "text.txt", info, "good", null);
        File.WriteAllText(Path.Combine(directory, "bad.json"), "{broken"); File.WriteAllText(Path.Combine(directory, "orphan.pending"), "partial");
        var listing = store.List(repo.Root); Assert.Single(listing.Drafts); Assert.Equal(1, listing.Unreadable);
        store.Clear(); Assert.Empty(store.List(repo.Root).Drafts); Assert.False(File.Exists(Path.Combine(directory, "orphan.pending"))); Assert.True(store.Enabled);
    }
    [Fact] public async Task Expired_content_is_reported_and_only_explicit_clear_removes_it()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); Store(); var now = DateTimeOffset.UtcNow;
        var old = new TextDraftStore(directory, () => now - TimeSpan.FromDays(8), null); var saved = old.Put(repo.Root, "text.txt", info, "old", null);
        var current = new TextDraftStore(directory, () => now, null); Assert.False(current.Contains(repo.Root, saved)); Assert.Equal(1, current.List(repo.Root).Expired); Assert.Single(Directory.GetFiles(directory, "*.json"));
        current.Clear(); Assert.Empty(Directory.GetFiles(directory, "*.json"));
    }
    [Fact] public async Task Unix_permissions_are_private_and_linked_record_is_not_read_or_deleted()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store(); store.Put(repo.Root, "text.txt", info, "private", null);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Assert.Single(Directory.GetFiles(directory, "*.json"))));
        var target = Path.Combine(repo.Root, "text.txt"); File.CreateSymbolicLink(Path.Combine(directory, "link.json"), target);
        Assert.Equal(1, store.List(repo.Root).Unreadable); Assert.Throws<IOException>(() => store.Clear()); Assert.Equal("base\n", File.ReadAllText(target));
    }
    [Fact] public async Task Final_close_verification_rejects_deleted_or_replaced_checkpoint()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store();
        var saved = store.Put(repo.Root, "text.txt", info, "draft", null); Assert.True(store.Contains(repo.Root, saved));
        var replacement = store.Put(repo.Root, "text.txt", info, "newer", saved.Revision); Assert.False(store.Contains(repo.Root, saved));
        store.Clear(); Assert.False(store.Contains(repo.Root, replacement));
    }
    [Fact] public async Task Interrupted_pending_files_count_against_total_storage_budget()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store();
        using(var pending = File.Create(Path.Combine(directory, "interrupted.pending"))) pending.SetLength(TextDraftStore.MaxStoreBytes);
        Assert.Throws<IOException>(() => store.Put(repo.Root, "text.txt", info, "draft", null));
        Assert.Empty(store.List(repo.Root).Drafts);
    }
    [Fact] public async Task Tampered_record_identity_is_not_restored()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store();
        var saved = store.Put(repo.Root, "text.txt", info, "draft", null);
        var file = Assert.Single(Directory.GetFiles(directory, "*.json"));
        File.WriteAllText(file, JsonSerializer.Serialize(saved with { Path = "other.txt" }));
        Assert.Empty(store.List(repo.Root).Drafts); Assert.Equal(1, store.List(repo.Root).Unreadable);
    }
    [Fact] public async Task Store_busy_fails_without_overwriting_existing_checkpoint()
    {
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store(); var saved = store.Put(repo.Root, "text.txt", info, "first", null);
        using(var gate = new FileStream(Path.Combine(directory, "store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.Put(repo.Root, "text.txt", info, "second", saved.Revision));
        Assert.Equal(saved, Assert.Single(store.List(repo.Root).Drafts));
    }

}
