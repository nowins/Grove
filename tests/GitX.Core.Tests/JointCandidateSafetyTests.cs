using System.Text.Json;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;
using Xunit;
namespace GitX.Core.Tests;
public sealed class JointCandidateSafetyTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Restore_ack_is_an_object_on_both_webview_boundaries(bool restored)
    {
        var raw = JsonSerializer.Serialize(new { restored });
        foreach (var value in new[] { raw, JsonSerializer.Serialize(raw) })
        {
            using var decoded = JsonDocument.Parse(WebViewScript.DecodeObject(value)!);
            Assert.Equal(restored, decoded.RootElement.GetProperty("restored").GetBoolean());
        }
        Assert.Null(WebViewScript.DecodeObject(restored ? "true" : "false"));
    }
    [Theory]
    [InlineData("{}")][InlineData("{\"restored\":null}")][InlineData("null")]
    public void Missing_restore_ack_is_unknown_not_a_negative_confirmation(string raw)
    {
        var json = WebViewScript.DecodeObject(raw);
        var ack = json is null ? null : JsonSerializer.Deserialize<DraftRestoreResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Null(ack?.Restored);
    }
    [Theory]
    [InlineData(false, true, false)] [InlineData(false, false, true)]
    [InlineData(true, true, false)] [InlineData(true, false, true)]
    public void Restore_guard_checks_real_paths_including_multifile_recovery(bool multiple, bool dirty, bool busy)
    {
        using var repo = new TestRepository();
        var recovery = new DiscardRecovery(repo.Root, multiple ? "历史文件操作 · 2 个文件" : "a.txt", "unused", true, "token",
            multiple ? [new("a.txt", true, "a"), new("b.txt", true, "b")] : null);
        var session = new DraftWriteSession(WorkingTextFiles.RootKey(repo.Root), multiple ? "b.txt" : "a.txt", dirty, busy);
        Assert.False(DraftWriteGuard.CanRestore(recovery, [session]));
        Assert.True(DraftWriteGuard.CanRestore(recovery, [session with { Dirty = false, Busy = false }]));
        Assert.True(DraftWriteGuard.CanRestore(recovery, [session with { RootKey = "other" }]));
        Assert.True(DraftWriteGuard.CanRestore(recovery, [session with { Path = "unrelated" }]));
    }
    [Fact]
    public async Task Revert_then_recovered_draft_blocks_undo_without_losing_either_copy()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base\n"); repo.Commit("base"); repo.Write("a.txt", "disk\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        var request = new DocumentRequest(repo.Root, "a.txt", ComparisonKind.LocalChanges);
        await model.RevertBlockAsync(request, await new RepositoryReader().DocumentAsync(request), 0);
        var baseline = (await new RepositoryReader().DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.WorkingFile))).Editing!;
        var store = new TextDraftStore(Path.Combine(repo.Container, "drafts")); store.SetEnabled(true);
        var record = store.Put(repo.Root, "a.txt", baseline, "recovered draft", null);
        Assert.False(DraftWriteGuard.CanRestore(model.CurrentRecovery!, [new(baseline.RootKey, "a.txt", true, false)]));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt"))); Assert.True(store.Contains(repo.Root, record));
        Assert.True(model.HasRecovery);
        Assert.True(DraftWriteGuard.CanRestore(model.CurrentRecovery!, [])); await model.RestoreDiscardAsync();
        Assert.False(model.HasError); Assert.Equal("disk\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt"))); Assert.True(store.Contains(repo.Root, record));
    }
    [Fact]
    public async Task Draft_identity_survives_git_directory_metadata_changes_but_not_replacement()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base");
        var identity = TextDraftStore.RepositoryIdentity(repo.Root);
        var baseline = (await new RepositoryReader().DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.WorkingFile))).Editing!;
        var store = new TextDraftStore(Path.Combine(repo.Container, "drafts")); store.SetEnabled(true);
        store.Put(repo.Root, "a.txt", baseline, "old repository draft", null);
        var git = Path.Combine(repo.Root, ".git");
        Directory.SetLastWriteTimeUtc(git, DateTime.UtcNow.AddDays(-2));
        repo.Git("config", "test.draftIdentity", "updated");
        Assert.Equal(identity, TextDraftStore.RepositoryIdentity(repo.Root));
        var old = Path.Combine(repo.Container, "old-git"); Directory.Move(git, old);
        repo.Git("init", "-b", "main");
        // The replacement .git has no fixture-local identity or signing settings.
        // Keep this test independent of the runner's global Git configuration.
        repo.Git("config", "user.name", "GitX Test");
        repo.Git("config", "user.email", "gitx-test@example.invalid");
        repo.Git("config", "commit.gpgsign", "false");
        repo.Git("config", "core.autocrlf", "false");
        repo.Write("a.txt", "new"); repo.Commit("replacement");
        Assert.NotEqual(identity, TextDraftStore.RepositoryIdentity(repo.Root));
        Assert.Empty(store.List(repo.Root).Drafts); Assert.Equal(1, store.List(repo.Root).Unassigned);
        Assert.Single(Directory.GetFiles(Path.Combine(repo.Container, "drafts"), "*.json"));
        await Task.CompletedTask;
    }
    [Fact]
    public void Search_palette_excludes_draft_and_git_write_commands()
    {
        var ids = ShellCommands.SearchActions("", false, _ => true).Select(c => c.Id).ToHashSet();
        foreach (var id in new[] { ShellCommandId.DraftRecovery, ShellCommandId.RestoreDiscard, ShellCommandId.Stage, ShellCommandId.Unstage, ShellCommandId.Sync, ShellCommandId.SaveFile })
            Assert.DoesNotContain(id, ids);
    }
    [Fact]
    public async Task Push_write_state_stays_active_until_completion_without_touching_draft_copy()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "base"); repo.Commit("base");
        var baseline = (await new RepositoryReader().DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.WorkingFile))).Editing!;
        var store = new TextDraftStore(Path.Combine(repo.Container, "drafts")); store.SetEnabled(true);
        var draft = store.Put(repo.Root, "a.txt", baseline, "dirty", null);
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = model.ExecuteActionAsync(repo.Root, async () => { entered.SetResult(); await finish.Task; return "done"; }, "Push");
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(model.IsWriting); Assert.True(store.Contains(repo.Root, draft)); }
        finally { finish.SetResult(); await action; }
        Assert.False(model.IsWriting); Assert.False(model.HasError); Assert.True(store.Contains(repo.Root, draft));
    }
    [Fact]
    public async Task Search_and_push_never_publish_persisted_or_unsaved_worktree_drafts()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "committed\n"); repo.Commit("base"); repo.Write("a.txt", "disk only\n");
        var baseline = (await new RepositoryReader().DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.WorkingFile))).Editing!;
        var store = new TextDraftStore(Path.Combine(repo.Container, "drafts")); store.SetEnabled(true);
        var draft = store.Put(repo.Root, "a.txt", baseline, "private unsaved draft", null);
        var remotePath = Path.Combine(repo.Container, "remote.git"); repo.Git("init", "--bare", remotePath); repo.Git("remote", "add", "origin", remotePath);
        var head = repo.Git("rev-parse", "HEAD"); var index = repo.Git("write-tree");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); var tabs = model.SourceTabs.Count;
        var preview = await RepositoryReader.SearchPreviewAsync(repo.Root, new("a.txt", 1, 1, 4, "disk"));
        Assert.Contains("disk only", preview.Text); Assert.DoesNotContain("private", preview.Text); Assert.Equal(tabs, model.SourceTabs.Count);
        var remote = Assert.Single(await new RemoteWriter().ReadAsync(repo.Root)); var reader = new PushPreviewReader();
        var push = await reader.ReadAsync(await RepositoryActions.ReadAsync(repo.Root), remote, "main"); await reader.ExecuteAsync(push, false);
        Assert.Equal("committed", repo.Git("--git-dir=" + remotePath, "show", "main:a.txt").Trim());
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, repo.Git("write-tree"));
        Assert.Equal("disk only\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt"))); Assert.True(store.Contains(repo.Root, draft));
    }
}
