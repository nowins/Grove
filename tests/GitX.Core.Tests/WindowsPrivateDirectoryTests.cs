using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class WindowsPrivateDirectoryTests
{
    [Fact]
    public async Task Save_needs_only_modify_permission_on_parent_and_keeps_private_staging()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new TestRepository();
        CreateModifyParent(Path.Combine(repo.Root, "editable"));
        repo.Write("editable/text.txt", "base\n"); repo.Commit("base");
        var head = repo.Git("rev-parse", "HEAD"); var index = Hash(Path.Combine(repo.Root, ".git", "index"));
        var doc = await new RepositoryReader().DocumentAsync(new(repo.Root, "editable/text.txt", ComparisonKind.WorkingFile));
        var inspected = false;
        var writer = new WorkingTextWriter(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
            var folder = new DirectoryInfo(Assert.Single(Directory.GetDirectories(Path.Combine(repo.Root, "editable"), ".grove-save-*")));
            AssertPrivate(folder); inspected = true;
            return Task.CompletedTask;
        });
        await writer.SaveAsync(repo.Root, "editable/text.txt", doc.Editing!.Fingerprint, "已保存\n");
        Assert.True(inspected); Assert.Equal("已保存\n", File.ReadAllText(Path.Combine(repo.Root, "editable", "text.txt")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(repo.Root, "editable"), ".grove-save-*"));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, Hash(Path.Combine(repo.Root, ".git", "index")));
    }

    [Fact]
    public async Task Fresh_draft_store_below_modify_parent_initializes_and_roundtrips_privately()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new TestRepository(); repo.Write("text.txt", "base\n"); repo.Commit("base");
        var head = repo.Git("rev-parse", "HEAD"); var index = Hash(Path.Combine(repo.Root, ".git", "index"));
        var parent = Path.Combine(repo.Root, "profile"); CreateModifyParent(parent);
        var directory = Path.Combine(parent, "DraftRecovery"); var store = new TextDraftStore(directory);
        Assert.False(store.Enabled); Assert.Empty(store.List(repo.Root).Drafts); AssertPrivate(new DirectoryInfo(directory));
        store.SetEnabled(true);
        var doc = await new RepositoryReader().DocumentAsync(new(repo.Root, "text.txt", ComparisonKind.WorkingFile));
        var draft = store.Put(repo.Root, "text.txt", doc.Editing!, "恢复内容", null);
        Assert.Equal(draft, Assert.Single(new TextDraftStore(directory).List(repo.Root).Drafts));
        foreach (var file in Directory.GetFiles(directory)) AssertPrivate(new FileInfo(file));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(repo.Root, "text.txt")));
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, Hash(Path.Combine(repo.Root, ".git", "index")));
    }

    [Fact]
    public void Empty_inherited_directory_left_by_failed_initialization_can_be_recreated_privately()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new TestRepository(); var parent = Path.Combine(repo.Root, "profile"); CreateModifyParent(parent);
        var directory = Path.Combine(parent, "DraftRecovery"); Directory.CreateDirectory(directory);
        Assert.False(new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected);
        var store = new TextDraftStore(directory);
        Assert.False(store.Enabled); AssertPrivate(new DirectoryInfo(directory));
        Assert.Empty(store.List(repo.Root).Drafts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_unsafe_directory_with_data_or_explicit_rules_is_not_repaired(bool explicitRule)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var repo = new TestRepository(); var parent = Path.Combine(repo.Root, "profile"); CreateModifyParent(parent);
        var directory = Path.Combine(parent, "DraftRecovery"); Directory.CreateDirectory(directory);
        var folder = new DirectoryInfo(directory);
        if (explicitRule)
        {
            var security = folder.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            folder.SetAccessControl(security);
        }
        else File.WriteAllText(Path.Combine(directory, "keep.json"), "preserve");
        var before = folder.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Throws<IOException>(() => new TextDraftStore(directory).List(repo.Root));
        Assert.Equal(before, folder.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
        if (!explicitRule) Assert.Equal("preserve", File.ReadAllText(Path.Combine(directory, "keep.json")));
    }

    [SupportedOSPlatform("windows")]
    private static void CreateModifyParent(string path)
    {
        var user = WindowsIdentity.GetCurrent().User!; var security = new DirectorySecurity();
        security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPrivate(FileSystemInfo item)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        FileSystemSecurity security = item is DirectoryInfo folder ? folder.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier))); Assert.True(security.AreAccessRulesProtected);
        Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
        { Assert.Equal(user, rule.IdentityReference); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); });
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
