using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ReadingHistoryTests
{
    private static ReadingTarget Target(string path = "a.cs", string? revision = null) => new(path, ComparisonKind.WorkingFile, null, null, null, revision);
    private static ReadingPlace Place(string path, int line, int side = 1) => new(Target(path), side, line, 1);

    [Fact]
    public void Typing_arrows_and_same_line_navigation_update_the_current_place_without_new_entries()
    {
        var history = new ReadingHistory();
        history.Record(Place("a.cs", 1), false); history.Record(Place("a.cs", 300), false);
        Assert.Single(history.Places);
        history.Record(Place("a.cs", 40), true);
        history.Record(new(Target(), 1, 40, 8), true);
        Assert.Equal([300, 40], history.Places.Select(place => place.Line));
        Assert.Equal(8, history.Current!.Column);
    }

    [Fact]
    public void A_new_jump_drops_forward_entries_and_nearby_departures_merge()
    {
        var history = new ReadingHistory();
        history.Record(Place("a.cs", 10), false); history.Record(Place("a.cs", 50), true);
        history.Record(Place("a.cs", 12), false); history.Record(Place("a.cs", 80), true);
        Assert.Equal([12, 80], history.Places.Select(place => place.Line));
        var back = history.Peek(-1, false)!.Value; Assert.True(history.Move(back.Index, history.Generation));
        Assert.True(history.CanForward);
        history.Record(Place("a.cs", 14), false); Assert.True(history.CanForward);
        history.Record(Place("b.cs", 20), false); Assert.False(history.CanForward);
        Assert.Equal(["a.cs", "b.cs"], history.Places.Select(place => place.Target.Path));
    }

    [Fact]
    public void History_is_bounded_and_moves_reject_a_superseded_generation()
    {
        var history = new ReadingHistory();
        for (var i = 0; i < 150; i++) history.Record(Place($"file{i}.cs", 1), false);
        Assert.Equal(100, history.Places.Count); Assert.Equal("file50.cs", history.Places[0].Target.Path);
        var generation = history.Generation; var back = history.Peek(-1, false)!.Value;
        history.Record(Place("file149.cs", 2), false);
        Assert.False(history.Move(back.Index, generation)); Assert.Equal("file149.cs", history.Current!.Target.Path);
        Assert.Null(history.Peek(0, false)); history.Clear(); Assert.Null(history.Current); Assert.False(history.CanBack);
    }

    [Fact]
    public void Same_path_revisions_and_diff_sides_are_distinct_places()
    {
        var history = new ReadingHistory();
        history.Record(new(Target(), 1, 3, 1), false);
        history.Record(new(Target(revision: new('a', 40)), 1, 3, 1), false);
        history.Record(new(Target(revision: new('a', 40)), 0, 3, 1), true);
        Assert.Equal(3, history.Places.Count);
    }

    [Fact]
    public async Task Real_files_restore_coordinates_reopen_closed_tabs_and_do_not_write_git()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "first\nsecond\nthird\n"); repo.Write("b.cs", "one\ntwo\n"); repo.Commit("base");
        var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")); var head = repo.Git("rev-parse", "HEAD");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile));
        model.RecordReadingPosition(new(model.DocumentVersion, 1, 3, 2));
        await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        model.RecordReadingPosition(new(model.DocumentVersion, 1, 2, 3));
        Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion));
        Assert.Equal("a.cs", model.CurrentDocument!.Path); Assert.Equal(3, model.CurrentDocument.RevealLine); Assert.Equal(2, model.CurrentDocument.RevealColumn);
        Assert.True(model.CanReadForward); Assert.True(await model.NavigateReadingAsync(1, model.DocumentVersion));
        Assert.Equal("b.cs", model.CurrentDocument!.Path); Assert.Equal(2, model.CurrentDocument.RevealLine);
        await model.CloseSourceTabsAsync(model.ActiveSourceTabId!, model.DocumentVersion, SourceTabCloseScope.All);
        Assert.Empty(model.SourceTabs); Assert.True(model.CanReadBack);
        Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion));
        Assert.Equal("b.cs", model.CurrentDocument!.Path); Assert.Single(model.SourceTabs); Assert.NotNull(model.CurrentDocument.Editing);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git", "index")));
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Same_path_historical_source_stays_on_its_fixed_revision_and_read_only()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "old\nkeep\n"); repo.Commit("old"); var old = repo.Git("rev-parse", "HEAD").Trim();
        repo.Write("a.cs", "new\nkeep\n"); repo.Commit("new");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); await model.OpenRevisionSourceAsync("a.cs", old);
        model.RecordReadingPosition(new(model.DocumentVersion, 1, 2, 2));
        await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile));
        Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion));
        Assert.Equal(old, model.CurrentDocument!.SourceRevision); Assert.Equal("old\nkeep\n", model.CurrentDocument.Text);
        Assert.Null(model.CurrentDocument.Editing); Assert.Equal(2, model.CurrentDocument.RevealLine); Assert.Equal(2, model.SourceTabs.Count);
        Assert.True(await model.NavigateReadingAsync(1, model.DocumentVersion));
        Assert.Null(model.CurrentDocument!.SourceRevision); Assert.NotNull(model.CurrentDocument.Editing);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Closing_captures_the_last_live_cursor_and_rejects_a_superseded_capture(bool supersede)
    {
        var reader = new ControlledReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile)); var version = model.DocumentVersion;
        var pending = new TaskCompletionSource<ReadingPosition?>(TaskCreationOptions.RunContinuationsAsynchronously); var captures = 0;
        model.CaptureReadingPosition = _ => ++captures == 1 ? pending.Task : Task.FromResult<ReadingPosition?>(null);
        var close = model.CloseSourceTabsAsync(model.ActiveSourceTabId!, version, SourceTabCloseScope.All);
        if (supersede) await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        pending.SetResult(new(version, 1, 40, 9)); await close;
        if (supersede)
        {
            Assert.Equal("b.cs", model.CurrentDocument!.Path); Assert.Equal(2, model.SourceTabs.Count);
        }
        else
        {
            Assert.Empty(model.SourceTabs); Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion));
            Assert.Equal("a.cs", model.CurrentDocument!.Path); Assert.Equal(40, model.CurrentDocument.RevealLine); Assert.Equal(9, model.CurrentDocument.RevealColumn);
        }
    }

    [Fact]
    public async Task Missing_target_leaves_current_document_tabs_and_history_index_unchanged()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "a\n"); repo.Write("b.cs", "b\n"); repo.Commit("base");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile)); await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        var document = model.CurrentDocument; var version = model.DocumentVersion; var tab = model.ActiveSourceTabId;
        File.Delete(Path.Combine(repo.Root, "a.cs"));
        Assert.False(await model.NavigateReadingAsync(-1, version));
        Assert.Same(document, model.CurrentDocument); Assert.Equal(version, model.DocumentVersion); Assert.Equal(tab, model.ActiveSourceTabId);
        Assert.True(model.CanReadBack); Assert.False(model.CanReadForward);
        repo.Write("a.cs", "restored\n"); Assert.True(await model.NavigateReadingAsync(-1, version));
        Assert.Equal("restored\n", model.CurrentDocument!.Text);
    }

    [Fact]
    public async Task Repository_sessions_clear_history_even_when_reopening_the_same_root()
    {
        using var a = new TestRepository(); a.Write("a.cs", "a\n"); a.Write("b.cs", "b\n"); a.Commit("base");
        using var b = new TestRepository(); b.Write("a.cs", "other\n"); b.Commit("base");
        using var model = new MainViewModel(); await model.OpenAsync(a.Root);
        await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile)); await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        var oldVersion = model.DocumentVersion; Assert.True(model.CanReadBack);
        await model.OpenAsync(b.Root); Assert.False(model.CanReadBack); Assert.False(model.CanReadForward);
        Assert.False(await model.NavigateReadingAsync(-1, oldVersion));
        await model.OpenAsync(a.Root); Assert.False(model.CanReadBack);
    }

    [Fact]
    public async Task A_late_capture_cannot_open_a_file_after_a_newer_navigation()
    {
        var reader = new ControlledReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile)); var version = model.DocumentVersion;
        var captures = new List<TaskCompletionSource<ReadingPosition?>>();
        model.CaptureReadingPosition = _ => { var capture = new TaskCompletionSource<ReadingPosition?>(TaskCreationOptions.RunContinuationsAsynchronously); captures.Add(capture); return capture.Task; };
        var b = model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        var c = model.OpenEditorEntryAsync(new("c.cs", "c", ComparisonKind.WorkingFile));
        captures[1].SetResult(new(version, 1, 8, 3)); await c;
        captures[0].SetResult(new(version, 1, 1, 1)); await b;
        Assert.Equal("c.cs", model.CurrentDocument!.Path); Assert.DoesNotContain("b.cs", reader.Reads);
        model.CaptureReadingPosition = null;
        Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion)); Assert.Equal(8, model.CurrentDocument!.RevealLine);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_late_history_read_cannot_replace_a_new_file_or_closed_model(bool close)
    {
        var reader = new ControlledReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile)); await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        var pending = new TaskCompletionSource<RepositoryDocument>(TaskCreationOptions.RunContinuationsAsynchronously); reader.Delay = pending;
        var back = model.NavigateReadingAsync(-1, model.DocumentVersion);
        if (close) model.Dispose(); else await model.OpenEditorEntryAsync(new("c.cs", "c", ComparisonKind.WorkingFile));
        pending.SetResult(new("a.cs", "late", "plaintext", "working", ""));
        Assert.False(await back); Assert.NotEqual("a.cs", model.CurrentDocument?.Path);
    }

    [Fact]
    public async Task New_cursor_navigation_supersedes_a_pending_history_read_and_modal_capture_blocks_it()
    {
        var reader = new ControlledReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        await model.OpenEditorEntryAsync(new("a.cs", "a", ComparisonKind.WorkingFile)); await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        model.CaptureReadingPosition = _ => Task.FromResult<ReadingPosition?>(null);
        Assert.False(await model.NavigateReadingAsync(-1, model.DocumentVersion)); Assert.Equal("b.cs", model.CurrentDocument!.Path);
        model.CaptureReadingPosition = null;
        var pending = new TaskCompletionSource<RepositoryDocument>(TaskCreationOptions.RunContinuationsAsynchronously); reader.Delay = pending;
        var back = model.NavigateReadingAsync(-1, model.DocumentVersion);
        model.RecordReadingPosition(new(model.DocumentVersion, 1, 40, 1), navigation: true);
        pending.SetResult(new("a.cs", "late", "plaintext", "working", ""));
        Assert.False(await back); Assert.Equal("b.cs", model.CurrentDocument!.Path);
    }

    [Fact]
    public async Task Diff_history_restores_the_selected_source_side_without_changing_git()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "one\ntwo\nthree\nfour\n"); repo.Write("b.cs", "b\n"); repo.Commit("base");
        repo.Write("a.cs", "one\ntwo\nupdated\nfour\n"); var state = repo.Git("status", "--porcelain");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("a.cs", "changes", ComparisonKind.LocalChanges));
        model.RecordReadingPosition(new(model.DocumentVersion, 0, 3, 2));
        await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion));
        Assert.True(model.CurrentDocument!.IsDiff); Assert.Equal(0, model.CurrentDocument.RevealSide);
        Assert.Equal(3, model.CurrentDocument.RevealLine); Assert.Equal(2, model.CurrentDocument.RevealColumn);
        Assert.Contains("three", model.CurrentDocument.Diff!.Old.Text); Assert.Contains("updated", model.CurrentDocument.Diff.New.Text);
        Assert.Equal(state, repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task Previous_blame_revision_keeps_its_old_path_after_a_rename()
    {
        using var repo = new TestRepository(); repo.Write("old.cs", "old\nkeep 1\nkeep 2\nkeep 3\n"); repo.Write("b.cs", "b\n"); repo.Commit("base");
        var old = repo.Git("rev-parse", "HEAD").Trim(); repo.Git("mv", "old.cs", "new.cs");
        repo.Write("new.cs", "new\nkeep 1\nkeep 2\nkeep 3\n"); repo.Commit("rename and edit");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(new("new.cs", "new", ComparisonKind.WorkingFile)); await model.ToggleBlameAsync();
        Assert.True(model.BlameLineAt(1, model.DocumentVersion)?.HasPrevious);
        await model.PreviousAnnotationAsync(1, model.DocumentVersion);
        Assert.Equal(old, model.CurrentDocument!.SourceRevision); Assert.Equal("old.cs", model.CurrentDocument.Path);
        model.RecordReadingPosition(new(model.DocumentVersion, 1, 2, 2));
        await model.OpenEditorEntryAsync(new("b.cs", "b", ComparisonKind.WorkingFile));
        Assert.True(await model.NavigateReadingAsync(-1, model.DocumentVersion));
        Assert.Equal("old.cs", model.CurrentDocument!.Path); Assert.Equal(old, model.CurrentDocument.SourceRevision);
        Assert.Null(model.CurrentDocument.Editing); Assert.Equal(2, model.CurrentDocument.RevealLine);
    }

    private sealed class ControlledReader : IRepositoryReader
    {
        public List<string> Reads { get; } = [];
        public TaskCompletionSource<RepositoryDocument>? Delay { get; set; }
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", null, [new("a.cs"), new("b.cs"), new("c.cs")], [], false));
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default)
        {
            Reads.Add(request.Path);
            if (request.Path == "a.cs" && Delay is { } delay) { Delay = null; return delay.Task; }
            return Task.FromResult(new RepositoryDocument(request.Path, "line\n", "plaintext", "working", ""));
        }
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage([], false));
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
    }
}
