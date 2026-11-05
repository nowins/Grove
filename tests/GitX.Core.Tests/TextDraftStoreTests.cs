using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
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
        using (var bad = CreatePrivateFixture(Path.Combine(directory, "bad.json"))) bad.Write("{broken"u8);
        using (var pending = CreatePrivateFixture(Path.Combine(directory, "orphan.pending"))) pending.Write("partial"u8);
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
        using(var pending = CreatePrivateFixture(Path.Combine(directory, "interrupted.pending"))) pending.SetLength(TextDraftStore.MaxStoreBytes);
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

    [Fact] public async Task Windows_new_store_files_have_current_user_owner_and_private_acl_before_install()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store();
        AssertPrivateWindowsAcl(new DirectoryInfo(directory));
        AssertPrivateWindowsAcl(new FileInfo(Path.Combine(directory, "store.lock")));
        AssertPrivateWindowsAcl(new FileInfo(Path.Combine(directory, "enabled")));
        var inspectedPending = false;
        var checking = new TextDraftStore(directory, () => DateTimeOffset.UtcNow, () =>
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows ACL fixture required.");
            AssertPrivateWindowsAcl(new FileInfo(Assert.Single(Directory.GetFiles(directory, "*.pending"))));
            inspectedPending = true;
        });
        var saved = checking.Put(repo.Root, "text.txt", info, "private", null);
        Assert.True(inspectedPending);
        AssertPrivateWindowsAcl(new FileInfo(Assert.Single(Directory.GetFiles(directory, "*.json"))));
        Assert.Equal(saved, Assert.Single(new TextDraftStore(directory).List(repo.Root).Drafts));
        store.SetEnabled(true); Assert.True(store.Enabled);
    }

    [Theory]
    [InlineData("store.lock")]
    [InlineData("enabled")]
    [InlineData("record")]
    public async Task Windows_existing_file_with_other_principal_is_rejected_without_acl_repair(string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new TestRepository(); var info = await Baseline(repo); var store = Store();
        store.Put(repo.Root, "text.txt", info, "private", null);
        var path = kind == "record" ? Assert.Single(Directory.GetFiles(directory, "*.json")) : Path.Combine(directory, kind);
        var file = new FileInfo(path); var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);
        var before = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        var bytes = File.ReadAllBytes(path);
        if (kind == "record") Assert.Throws<IOException>(() => store.Clear());
        else if (kind == "enabled") Assert.Throws<IOException>(() => { _ = store.Enabled; });
        else Assert.Throws<IOException>(() => store.SetEnabled(true));
        Assert.Equal(before, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact] public void Windows_existing_directory_with_other_principal_is_rejected_without_acl_repair()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store(); var folder = new DirectoryInfo(directory); var security = folder.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        folder.SetAccessControl(security);
        var before = folder.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Throws<IOException>(() => store.SetEnabled(true));
        Assert.Equal(before, folder.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPrivateWindowsAcl(FileSystemInfo item)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        FileSystemSecurity security = item is DirectoryInfo folder ? folder.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => { Assert.Equal(user, rule.IdentityReference); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); });
    }

    private static FileStream CreatePrivateFixture(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User!; var security = new FileSecurity();
            security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.None, security);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

}
