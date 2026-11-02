using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class NoteNavigationTests
{
    [Fact]
    public async Task Opens_original_source_across_files_reuses_tabs_and_keeps_filter_and_annotation()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "first\nrecorded\n"); repo.Write("b.cs", "other\n"); repo.Commit("base");
        using var reader = new RepositoryReader();
        var anchor = CommentAnchor.Capture(await reader.DocumentAsync(new(repo.Root, "a.cs", ComparisonKind.WorkingFile)), 1, 2, 2);
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        Assert.True(await model.OpenNoteSourceAsync(model.Root!, anchor));
        await model.ToggleBlameAsync(); Assert.NotEmpty(model.CurrentDocument!.Blame!);
        model.SelectedEntry = model.Entries.First(entry => entry.Path == "b.cs"); await UntilAsync(() => model.CurrentDocument?.Path == "b.cs");
        model.Filter = "hidden";
        Assert.True(await model.OpenNoteSourceAsync(model.Root!, anchor));
        Assert.Equal("hidden", model.Filter); Assert.Equal(2, model.SourceTabs.Count);
        Assert.Equal(2, model.CurrentDocument!.RevealLine); Assert.Equal(1, model.CurrentDocument.RevealColumn);
        Assert.NotEmpty(model.CurrentDocument.Blame!); Assert.True(anchor.Matches(model.CurrentDocument.CommentContext));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("head")]
    [InlineData("missing")]
    [InlineData("repository")]
    [InlineData("side")]
    [InlineData("comparison")]
    public async Task Changed_or_incompatible_source_keeps_the_current_document_and_tabs(string change)
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "recorded\n"); repo.Write("b.txt", "current\n"); repo.Commit("base");
        using var reader = new RepositoryReader();
        var anchor = CommentAnchor.Capture(await reader.DocumentAsync(new(repo.Root, "a.txt", ComparisonKind.WorkingFile)), 1, 1, 1);
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        model.SelectedEntry = model.Entries.First(entry => entry.Path == "b.txt"); await UntilAsync(() => model.CurrentDocument?.Path == "b.txt");
        var original = model.CurrentDocument; var version = model.DocumentVersion; var tabs = model.SourceTabs.ToArray();
        switch (change)
        {
            case "text": repo.Write("a.txt", "inserted\nrecorded\n"); break;
            case "head": repo.Write("other.txt", "new\n"); repo.Commit("new HEAD"); break;
            case "missing": File.Delete(Path.Combine(repo.Root, "a.txt")); break;
            case "repository": anchor = anchor with { Context = anchor.Context with { RepositoryId = new string('a', 64) } }; break;
            case "side": anchor = anchor with { Side = 0 }; break;
            case "comparison": anchor = anchor with { Context = anchor.Context with { Kind = ComparisonKind.Worktree } }; break;
        }
        Assert.False(await model.OpenNoteSourceAsync(model.Root!, anchor));
        Assert.Same(original, model.CurrentDocument); Assert.Equal(version, model.DocumentVersion); Assert.Equal(tabs, model.SourceTabs);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("repository")]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("newer")]
    public async Task Late_preflight_cannot_publish_after_navigation_close_or_draft_edit(string change)
    {
        var reader = new PendingReader(); using var model = new MainViewModel(reader); await model.OpenAsync("repo");
        var context = new CommentContext("repo-id", "repo", "a.txt", ComparisonKind.WorkingFile, "head", "snapshot");
        var anchor = new CommentAnchor(context, 1, "a.txt", "工作区", 1, 1, "recorded", "plaintext");
        var opening = model.OpenNoteSourceAsync("repo", anchor); var old = reader.Reads[0];
        var document = new RepositoryDocument("a.txt", "recorded", "plaintext", "working", "") { CommentContext = context };
        if (change == "file")
        {
            model.SelectedEntry = model.Entries.First(entry => entry.Path == "b.txt");
            reader.Reads[1].SetResult(new("b.txt", "current", "plaintext", "working", "")); await UntilAsync(() => model.CurrentDocument?.Path == "b.txt");
        }
        else if (change == "repository") await model.OpenAsync("other");
        else if (change == "close") model.Dispose();
        else if (change == "cancel") model.CancelNoteSource();
        else
        {
            var newer = model.OpenNoteSourceAsync("repo", anchor);
            reader.Reads[1].SetResult(document with { Text = "latest", RevealLine = 1 }); Assert.True(await newer);
        }
        var current = model.CurrentDocument; var version = model.DocumentVersion;
        old.SetResult(document); Assert.False(await opening);
        Assert.Same(current, model.CurrentDocument); Assert.Equal(version, model.DocumentVersion);
    }

    private static async Task UntilAsync(Func<bool> ready) { for (var i = 0; i < 100 && !ready(); i++) await Task.Delay(20); Assert.True(ready()); }
    private sealed class PendingReader : IRepositoryReader
    {
        public List<TaskCompletionSource<RepositoryDocument>> Reads { get; } = [];
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => Task.FromResult(new RepositorySnapshot(path, "main", null, [new("a.txt"), new("b.txt")], [], false));
        public Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default) { var read = new TaskCompletionSource<RepositoryDocument>(TaskCreationOptions.RunContinuationsAsynchronously); Reads.Add(read); return read.Task; }
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => Task.FromResult(new HistoryPage([], false));
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => Task.FromResult<IReadOnlyList<ChangeEntry>>([]);
    }
}
