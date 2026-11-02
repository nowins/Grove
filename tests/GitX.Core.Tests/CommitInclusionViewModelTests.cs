using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class CommitInclusionViewModelTests
{
    [Fact]
    public async Task Batch_toggle_handles_mixed_selection_once_and_does_not_stage_or_include_other_files()
    {
        using var repo = new TestRepository();
        foreach (var file in new[] { "a.txt", "b.txt", "c.txt" }) repo.Write(file, "before\n");
        repo.Commit("base");
        foreach (var file in new[] { "a.txt", "b.txt", "c.txt" }) repo.Write(file, "after\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(1);
        model.SelectedEntry = model.Entries.First(entry => entry.Path == "a.txt"); await Loaded(model);
        model.IncludeLine(new(1, 1), true, model.DocumentVersion);
        var index = repo.Git("write-tree"); var events = 0; model.InclusionChanged += () => events++;
        model.SetFilesIncluded(["a.txt", "a.txt", "b.txt", "missing.txt"]);
        Assert.True(model.InclusionState("a.txt")); Assert.True(model.InclusionState("b.txt"));
        Assert.False(model.InclusionState("c.txt")); Assert.False(model.InclusionState("missing.txt")); Assert.Equal(1, events);
        model.SetFilesIncluded(["a.txt", "b.txt"]);
        Assert.False(model.InclusionState("a.txt")); Assert.False(model.InclusionState("b.txt")); Assert.Equal(2, events);
        Assert.Equal(index, repo.Git("write-tree"));
        model.SetFilesIncluded(["b.txt"], true); model.SetFilesIncluded(["a.txt", "b.txt"], false);
        Assert.Equal(0, model.IncludedFileCount);
    }

    [Fact]
    public async Task File_and_line_inclusion_stay_local_and_survive_whitespace_view_changes()
    {
        using var repo = new TestRepository(); repo.Write("a.java", "a\nb\nc\nd\ne\n"); repo.Commit("base");
        repo.Write("a.java", "A\nb\nc\nd\nE\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(1);
        model.SelectedEntry = Assert.Single(model.Entries); await Loaded(model);
        var before = repo.Git("write-tree");
        model.IncludeLine(new(1, 1), true, model.DocumentVersion);
        Assert.Null(model.InclusionState("a.java"));
        Assert.Equal(new[] { new ChangeLine(1, 1) }, model.CurrentIncludedLines());
        await model.SetWhitespaceAsync((int)DiffWhitespace.Ignore);
        Assert.Equal(new[] { new ChangeLine(1, 1) }, model.CurrentIncludedLines());
        Assert.Equal(before, repo.Git("write-tree"));
        model.ToggleIncludeFile("a.java"); Assert.True(model.InclusionState("a.java"));
        model.ToggleIncludeFile("a.java"); Assert.False(model.InclusionState("a.java"));
        Assert.Empty(model.CurrentIncludedLines()); Assert.Equal(before, repo.Git("write-tree"));
    }

    [Fact]
    public async Task Select_all_in_a_filtered_list_does_not_include_hidden_files()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "a"); repo.Write("b.txt", "b"); repo.Commit("base");
        repo.Write("a.txt", "A"); repo.Write("b.txt", "B");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(1); model.Filter = "a.txt";
        model.ToggleIncludeAll();
        Assert.True(model.InclusionState("a.txt")); Assert.False(model.InclusionState("b.txt"));
        model.CommitMessage = "only visible file"; await model.CommitAsync();
        Assert.False(model.HasError); Assert.Equal("A", repo.Git("show", "HEAD:a.txt")); Assert.Equal("b", repo.Git("show", "HEAD:b.txt"));
        Assert.Equal("B", File.ReadAllText(Path.Combine(repo.Root, "b.txt"))); Assert.Empty(repo.Git("diff", "--cached"));
    }

    [Fact]
    public async Task Externally_changed_code_invalidates_a_partial_range_instead_of_reusing_line_numbers()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "a\nb\nc"); repo.Commit("base"); repo.Write("a.txt", "A\nb\nC");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(1); model.SelectedEntry = model.Entries.Single(); await Loaded(model);
        model.IncludeLine(new(1, 1), true, model.DocumentVersion);
        repo.Write("a.txt", "inserted\nA\nb\nC"); await model.RefreshAsync();
        Assert.Empty(model.CurrentIncludedLines()); Assert.False(model.InclusionState("a.txt")); Assert.Empty(repo.Git("diff", "--cached"));
    }

    [Fact]
    public async Task A_deletion_boundary_includes_its_source_lines_once_and_rejects_an_old_document()
    {
        using var repo = new TestRepository(); repo.Write("a.txt", "a\nremove one\nremove two\nb\nc\nd\ne\n"); repo.Commit("base");
        repo.Write("a.txt", "a\nb\nc\nd\nE\n");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root); model.SwitchView(1); model.SelectedEntry = model.Entries.Single(); await Loaded(model);
        var version = model.DocumentVersion; var deletion = model.CurrentDocument!.Diff!.Blocks.ToList().FindIndex(block => block.NewCount == 0);
        model.IncludeDeletion(deletion, true, version);
        Assert.Equal(new[] { new ChangeLine(0, 2), new ChangeLine(0, 3) }, model.CurrentIncludedLines().OrderBy(line => line.Line));
        Assert.Null(model.InclusionState("a.txt")); Assert.Empty(repo.Git("diff", "--cached"));
        model.IncludeDeletion(deletion, false, version - 1); Assert.Equal(2, model.CurrentIncludedLines().Count);
        model.CommitMessage = "only deletion"; await model.CommitAsync();
        Assert.False(model.HasError); Assert.Equal("a\nb\nc\nd\ne\n", repo.Git("show", "HEAD:a.txt"));
        Assert.Equal("a\nb\nc\nd\nE\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Empty(repo.Git("diff", "--cached"));
    }

    private static async Task Loaded(MainViewModel model)
    {
        for (var i = 0; i < 200 && model.CurrentDocument?.Diff is null; i++) await Task.Delay(15);
        Assert.NotNull(model.CurrentDocument?.Diff);
    }
}
