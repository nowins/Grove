using GitX.Core;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ProjectRevealTests
{
    [Fact]
    public void Revealing_a_nested_file_expands_ancestors_without_replacing_its_entry()
    {
        using var repo = new TestRepository();
        var entry = new NavigationEntry("src/订单管理/deep/file.ts", "", ComparisonKind.WorkingFile);
        var tree = new FileNavigation(); tree.Update(repo.Root, [entry], 0, false, null);
        tree.ExpandAll(false); Assert.DoesNotContain(tree.Rows, r => r.IsFile);
        var result = ProjectFileReveal.Resolve(repo.Root, new(repo.Root, entry.Path), [entry], "");
        Assert.Same(entry, result.Entry); var row = tree.ShowFile(result.Entry!);
        Assert.NotNull(row); Assert.Same(entry, row.Entry);
        Assert.Contains(tree.Rows, r => r.Path == "src/订单管理/deep" && r.IsExpanded);
    }

    [Fact]
    public void Filter_hidden_missing_and_other_worktree_targets_never_choose_another_file()
    {
        using var repo = new TestRepository();
        var entry = new NavigationEntry("src/a.ts", "", ComparisonKind.WorkingFile);
        var target = new FileHistoryTarget(repo.Root, entry.Path);
        var hidden = ProjectFileReveal.Resolve(repo.Root, target, [entry], "b.ts");
        Assert.Null(hidden.Entry); Assert.True(hidden.HiddenByFilter);
        Assert.Same(entry, ProjectFileReveal.Resolve(repo.Root, target, [entry], "A.TS").Entry);
        Assert.Null(ProjectFileReveal.Resolve(repo.Root, target with { Root = repo.Root + "-linked" }, [entry], "").Entry);
        Assert.Null(ProjectFileReveal.Resolve(repo.Root, target with { Path = "history-only.ts" }, [entry], "").Entry);
        Assert.Null(ProjectFileReveal.Resolve(repo.Root, null, [entry], "").Entry);
    }

    [Fact]
    public async Task Current_file_location_keeps_history_context_and_is_cleared_on_repository_switch()
    {
        using var repo = new TestRepository(); repo.Write("a.ts", "first\n"); repo.Commit("base");
        using var other = new TestRepository(); other.Write("a.ts", "other\n"); other.Commit("other");
        using var model = new MainViewModel(); await model.OpenAsync(repo.Root);
        await model.OpenRevisionSourceAsync("a.ts", repo.Git("rev-parse", "HEAD").Trim());
        var document = model.CurrentDocument; var version = model.DocumentVersion;
        Assert.Equal(new FileHistoryTarget(repo.Root, "a.ts"), model.CurrentProjectFile);
        var plan = ProjectFileReveal.Resolve(repo.Root, model.CurrentProjectFile, model.NavigationEntries(0), "");
        Assert.NotNull(plan.Entry); Assert.Same(document, model.CurrentDocument); Assert.Equal(version, model.DocumentVersion);
        await model.OpenAsync(other.Root); Assert.Null(model.CurrentProjectFile);
    }

    [Fact]
    public void Old_four_tool_layouts_preserve_numeric_ids_and_add_hidden_nonmodal_tools()
    {
        var layout = new ToolDockLayout();
        layout.Restore(new(1, [new(ToolId.Project, ToolEdge.Right, Visible:true), new(ToolId.Commit, ToolEdge.Left),
            new(ToolId.GitLog, ToolEdge.Top, Visible:true), new(ToolId.FileHistory, ToolEdge.Bottom)]));
        Assert.Equal(3, (int)ToolId.FileHistory); Assert.Equal(ToolEdge.Right, layout[ToolId.Project].Edge);
        Assert.Equal(ToolEdge.Top, layout[ToolId.GitLog].Edge);
        Assert.All(new[] {ToolId.Output, ToolId.Worktrees}, id => { Assert.False(layout[id].Visible); Assert.Equal(ToolEdge.Bottom, layout[id].Edge); });
        layout.Show(ToolId.Output); layout.Show(ToolId.Worktrees);
        Assert.False(layout[ToolId.Output].Visible); Assert.True(layout[ToolId.Worktrees].Visible);
    }
}
