using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class DiffFileNavigationTests
{
    [Fact]
    public async Task Next_and_previous_cross_files_reveal_the_correct_edge_and_wrap_the_scope_read_only()
    {
        using var repo = new TestRepository();
        var baseline = string.Join('\n', Enumerable.Range(1, 60).Select(line => $"line {line}")) + "\n";
        repo.Write("a.cs", baseline); repo.Write("b.cs", baseline); repo.Commit("base");
        repo.Write("a.cs", baseline.Replace("line 10", "changed 10").Replace("line 40", "changed 40"));
        repo.Write("b.cs", baseline.Replace("line 20", "changed 20").Replace("line 50", "changed 50"));
        var head = repo.Git("rev-parse", "HEAD"); var index = File.ReadAllBytes(Path.Combine(repo.Root, ".git/index")); var status = repo.Git("--no-optional-locks", "status", "--porcelain");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(model.NavigationEntries(1).Single(entry => entry.Path == "a.cs"));
        Assert.True(model.CanNavigateDiffFiles); var version = model.DocumentVersion;
        Assert.True(await model.NavigateDiffFileAsync(1, 1, version));
        Assert.Equal("b.cs", model.CurrentDocument!.Path); Assert.Equal(1, model.CurrentDocument.RevealDiffEdge);
        Assert.Equal(model.CurrentDocument.Diff!.Blocks[0].NewStart + 1, model.CurrentDocument.RevealLine);
        Assert.False(await model.NavigateDiffFileAsync(1, 1, version));
        Assert.True(await model.NavigateDiffFileAsync(-1, 0, model.DocumentVersion));
        Assert.Equal("a.cs", model.CurrentDocument!.Path); Assert.Equal(-1, model.CurrentDocument.RevealDiffEdge);
        Assert.Equal(0, model.CurrentDocument.RevealSide);
        Assert.Equal(model.CurrentDocument.Diff!.Blocks[^1].OldStart + 1, model.CurrentDocument.RevealLine);
        Assert.True(await model.NavigateDiffFileAsync(-1, 1, model.DocumentVersion)); Assert.Equal("b.cs", model.CurrentDocument!.Path);
        Assert.True(await model.NavigateDiffFileAsync(1, 1, model.DocumentVersion)); Assert.Equal("a.cs", model.CurrentDocument!.Path);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD")); Assert.Equal(index, File.ReadAllBytes(Path.Combine(repo.Root, ".git/index")));
        Assert.Equal(status, repo.Git("--no-optional-locks", "status", "--porcelain"));
    }

    [Fact]
    public async Task Navigation_moves_on_when_the_current_file_becomes_clean()
    {
        using var repo = new TestRepository(); repo.Write("a", "old\n"); repo.Write("b", "old\n"); repo.Commit("base");
        repo.Write("a", "new\n"); repo.Write("b", "new\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(model.NavigationEntries(1).Single(entry => entry.Path == "a"));
        repo.Write("a", "old\n"); await model.RefreshAsync();
        Assert.Empty(model.CurrentDocument!.Diff!.Blocks);
        Assert.True(await model.NavigateDiffFileAsync(1, 1, model.DocumentVersion)); Assert.Equal("b", model.CurrentDocument!.Path);
    }

    [Fact]
    public async Task Binary_and_empty_changes_are_skipped_without_opening_intermediate_tabs()
    {
        using var repo = new TestRepository(); repo.Write("a.cs", "old\n"); repo.Write("d.cs", "old\n");
        File.WriteAllBytes(Path.Combine(repo.Root, "b.bin"), [0, 1]); repo.Commit("base");
        repo.Write("a.cs", "new\n"); repo.Write("d.cs", "new\n");
        File.WriteAllBytes(Path.Combine(repo.Root, "b.bin"), [0, 2]); repo.Write("c.txt", "");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(model.NavigationEntries(1).Single(entry => entry.Path == "a.cs"));
        Assert.True(await model.NavigateDiffFileAsync(1, 1, model.DocumentVersion));
        Assert.Equal("d.cs", model.CurrentDocument!.Path); Assert.Equal(2, model.SourceTabs.Count);
        Assert.DoesNotContain(model.SourceTabs, tab => tab.Path is "b.bin" or "c.txt");
    }

    [Fact]
    public async Task A_history_tab_keeps_its_original_range_after_the_log_comparison_changes()
    {
        using var repo = new TestRepository(); repo.Write("a", "old\n"); repo.Write("b", "old\n"); repo.Commit("old");
        repo.Write("a", "range\n"); repo.Write("b", "range\n"); repo.Commit("range");
        repo.Write("c", "other\n"); repo.Commit("other");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        var old = model.Commits[2]; var selected = model.Commits[1];
        await model.CompareHistoryAsync(old, selected);
        await model.OpenEditorEntryAsync(model.HistoryEntries.Single(entry => entry.Path == "a"));
        await model.CompareHistoryAsync(selected, model.Commits[0]); model.SwitchView(0);
        Assert.True(await model.NavigateDiffFileAsync(1, 1, model.DocumentVersion));
        Assert.Equal("b", model.CurrentDocument!.Path); Assert.Equal("range\n", model.CurrentDocument.Diff!.New.Text);
        Assert.Equal(ComparisonKind.RevisionRange, model.SelectedEntry!.Kind);
        Assert.Equal(old.Sha, model.SelectedEntry.BaseCommit); Assert.Equal(selected.Sha, model.SelectedEntry.Commit);
    }

    [Fact]
    public async Task A_pinned_commit_opened_without_log_selection_resolves_only_that_commit()
    {
        using var repo = new TestRepository(); repo.Write("a", "old\n"); repo.Write("b", "old\n"); repo.Commit("old");
        repo.Write("a", "new\n"); repo.Write("b", "new\n"); repo.Commit("new");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); var sha = model.Commits[0].Sha;
        await model.OpenEditorEntryAsync(new("a", "", ComparisonKind.Commit, Commit: sha));
        Assert.True(model.CanNavigateDiffFiles); Assert.True(await model.NavigateDiffFileAsync(1, 1, model.DocumentVersion));
        Assert.Equal("b", model.CurrentDocument!.Path); Assert.Equal(sha, model.SelectedEntry!.Commit);
    }

    [Theory]
    [InlineData("file")] [InlineData("repository")] [InlineData("cancel")] [InlineData("refresh")]
    public async Task A_late_boundary_read_cannot_replace_newer_navigation(string action)
    {
        using var repo = new TestRepository(); repo.Write("a", "old\n"); repo.Write("b", "old\n"); repo.Commit("old");
        repo.Write("a", "changed\n"); repo.Write("b", "changed\n");
        using var other = new TestRepository(); other.Write("c", "other\n"); other.Commit("other");
        using var reader = new DelayedReader(); using var model = new MainViewModel(reader); await model.OpenAsync(repo.Root);
        await model.OpenEditorEntryAsync(model.NavigationEntries(1).Single(entry => entry.Path == "a"));
        reader.DelayPath = "b";
        var navigation = model.NavigateDiffFileAsync(1, 1, model.DocumentVersion); await reader.Started.Task;
        Assert.False(await model.NavigateDiffFileAsync(1, 1, model.DocumentVersion));
        if (action == "file") await model.OpenEditorEntryAsync(new("a", "source", ComparisonKind.WorkingFile));
        else if (action == "repository") await model.OpenAsync(other.Root);
        else if (action == "cancel") model.CancelDiffNavigation();
        else
        {
            repo.Write("a", "changed again\n"); await model.RefreshAsync();
            Assert.False(model.HasError, model.Error);
            Assert.Equal("changed again\n", model.CurrentDocument?.Diff?.New.Text);
        }
        var current = model.CurrentDocument; var version = model.DocumentVersion;
        reader.Gate.SetResult(); Assert.False(await navigation);
        Assert.Same(current, model.CurrentDocument); Assert.Equal(version, model.DocumentVersion);
        Assert.False(model.HasError, model.Error);
    }

    private sealed class DelayedReader : IRepositoryReader, IDisposable
    {
        private readonly RepositoryReader _reader = new();
        public string? DelayPath { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RepositorySnapshot> OpenAsync(string path, CancellationToken token = default) => _reader.OpenAsync(path, token);
        public Task<HistoryPage> HistoryAsync(string root, string? head, int skip = 0, CancellationToken token = default) => _reader.HistoryAsync(root, head, skip, token);
        public Task<IReadOnlyList<ChangeEntry>> CommitChangesAsync(string root, string sha, CancellationToken token = default) => _reader.CommitChangesAsync(root, sha, token);
        public async Task<RepositoryDocument> DocumentAsync(DocumentRequest request, CancellationToken token = default)
        {
            if (request.Path == DelayPath) { Started.TrySetResult(); await Gate.Task; }
            return await _reader.DocumentAsync(request, CancellationToken.None);
        }
        public void Dispose() => _reader.Dispose();
    }
}
