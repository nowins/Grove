using System.Collections.Specialized;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public class FileNavigationTests
{
    [Theory]
    [InlineData("未跟踪", "?")]
    [InlineData("Untracked", "?")]
    [InlineData("新增", "A")]
    public void Untracked_files_have_a_distinct_status_from_added_files(string status, string code)
    {
        var entry = new NavigationEntry("cache.whl", "", ComparisonKind.LocalChanges, Status: status);
        Assert.Equal(code, entry.StatusCode);
        var nav = new FileNavigation(); nav.Update("repo", [entry], 1, false, entry);
        Assert.Equal(code, nav.Rows.Single(row => row.IsFile).StatusCode);
    }

    private static NavigationEntry File(string path, ComparisonKind kind = ComparisonKind.WorkingFile) => new(path, path, kind);

    [Fact]
    public void Group_and_folder_refresh_can_preserve_collapse_until_explicit_file_reveal()
    {
        var nav = new FileNavigation(); var change = File("src/A.java", ComparisonKind.LocalChanges);
        nav.Update("repo", [change], 1, false, change);
        nav.Toggle(Assert.Single(nav.Rows, row => row.IsGroup));
        nav.Update("repo", [change], 1, false, change, revealSelected: false);
        Assert.DoesNotContain(nav.Rows, row => row.IsFile);
        Assert.Same(change, nav.ShowFile(change)!.Entry);
        var source = File(change.Path); nav.Update("repo", [source], 0, false, source);
        nav.Toggle(nav.Rows.Single(row => row.Path == "src"));
        nav.Update("repo", [source], 0, false, source, revealSelected: false);
        Assert.DoesNotContain(nav.Rows, row => row.IsFile);
        Assert.Same(source, nav.ShowFile(source)!.Entry);
    }

    [Fact]
    public void History_tree_compacts_directories_and_retains_its_own_expansion_state()
    {
        var nav = new FileNavigation();
        var entry = File("src/main/java/Channel.java", ComparisonKind.Commit) with { Commit = "first" };
        nav.Update("repo", [entry], 2, false, null);
        var directory = nav.Rows.Single(r => r.Path == "src/main/java");
        Assert.Equal("src/main/java", directory.Name);
        Assert.Same(entry, nav.Rows.Single(r => r.IsFile).Entry);
        Assert.Equal(directory, nav.Parent(nav.ShowFile(entry)!));
        nav.Toggle(directory);
        nav.Update("repo", [entry], 2, false, null);
        Assert.DoesNotContain(nav.Rows, r => r.IsFile);
        nav.Update("repo", [File(entry.Path)], 0, false, null);
        Assert.Equal(new[] { "repo", "src" }, nav.Rows.Select(r => r.Name));
        nav.Update("repo", [entry], 2, false, null);
        Assert.DoesNotContain(nav.Rows, r => r.IsFile);
        nav.Update("repo", [entry with { Commit = "second" }], 2, false, null);
        Assert.Single(nav.Rows, r => r.IsFile);
    }

    [Fact]
    public void Tree_preserves_exact_paths_and_expands_selected_ancestors()
    {
        var nav = new FileNavigation();
        var entries = new[] { File("src/A/同名.cs"), File("src/a/同名.cs"), File("docs/readme.md") };
        nav.Update("repo", entries, 0, false, null);
        Assert.Equal(new[] { "repo", "docs", "src" }, nav.Rows.Select(r => r.Name));
        var selected = nav.ShowFile(entries[1]);
        Assert.Same(entries[1], selected!.Entry);
        Assert.Contains(nav.Rows, r => r.IsDirectory && r.Path == "src/A" && !r.IsExpanded);
        Assert.DoesNotContain(nav.Rows, r => r.Entry == entries[0]);
        Assert.Equal("src/a", nav.Parent(selected)!.Path);
        nav.Toggle(nav.Parent(selected)!);
        Assert.DoesNotContain(nav.Rows, r => r.Entry == entries[1]);
    }

    [Fact]
    public void Filtering_reveals_matches_without_losing_normal_expansion_state()
    {
        var nav = new FileNavigation(); var entry = File("src/深层/Test.cs");
        nav.Update("repo", [entry], 0, false, null);
        nav.Update("repo", [entry], 0, true, null);
        Assert.Contains(nav.Rows, r => r.Entry == entry);
        nav.Toggle(nav.Rows.Single(r => r.IsDirectory && r.Path == "src"));
        Assert.DoesNotContain(nav.Rows, r => r.Entry == entry);
        nav.Update("repo", [entry], 0, false, null);
        Assert.DoesNotContain(nav.Rows, r => r.Entry == entry);
        nav.Update("repo", [], 0, true, null);
        Assert.Empty(nav.Rows);
    }

    [Fact]
    public void Selecting_filtered_file_reopens_temporarily_collapsed_ancestors()
    {
        var nav = new FileNavigation(); var entry = File("src/deep/Test.cs");
        nav.Update("repo", [entry], 0, true, null);
        nav.Toggle(nav.Rows.Single(r => r.Path == "src"));
        Assert.Same(entry, nav.ShowFile(entry)!.Entry);
    }

    [Fact]
    public void Partial_staging_keeps_distinct_entries_and_same_names_have_parent_labels()
    {
        var nav = new FileNavigation();
        var a = File("src/A/Same.cs", ComparisonKind.Worktree); var b = a with { Kind = ComparisonKind.Index };
        var c = File("src/B/Same.cs", ComparisonKind.Worktree);
        nav.Update("repo", [a, c, b], 1, false, a);
        Assert.Equal(2, nav.Rows.Count(r => r.IsGroup));
        Assert.Equal(new[] { a, c, b }, nav.Rows.Where(r => r.IsFile).Select(r => r.Entry));
        Assert.Equal("A", nav.ShowFile(a)!.ParentLabel); Assert.Equal("B", nav.ShowFile(c)!.ParentLabel);
        Assert.NotEqual(nav.ShowFile(a)!.Key, nav.ShowFile(b)!.Key);
    }

    [Fact]
    public void Small_refresh_keeps_collection_and_unchanged_rows_without_reset()
    {
        var nav = new FileNavigation(); var a = File("a.cs"); var b = File("b.cs");
        nav.Update("repo", [a, b], 2, false, a);
        var first = nav.Rows[0]; var resets = 0;
        ((INotifyCollectionChanged)nav.Rows).CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        nav.Update("repo", [a, b with { Status = "修改" }], 2, false, a);
        Assert.Same(first, nav.Rows[0]); Assert.Equal(0, resets);
    }

    [Fact]
    public void Status_refresh_updates_selected_row_in_place_and_notifies_bindings()
    {
        var nav = new FileNavigation(); var entry = File("src/A.cs", ComparisonKind.Worktree) with { Status = "修改" };
        nav.Update("repo", [entry], 1, false, entry);
        var row = nav.ShowFile(entry)!; var notices = 0; var changes = 0;
        row.PropertyChanged += (_, _) => notices++;
        ((INotifyCollectionChanged)nav.Rows).CollectionChanged += (_, _) => changes++;
        var changed = entry with { Status = "删除", Subtitle = "new status" };
        nav.Update("repo", [changed], 1, false, changed);
        Assert.Same(row, nav.ShowFile(changed)); Assert.Equal("D", row.StatusCode);
        Assert.Equal(1, notices); Assert.Equal(0, changes);
    }

    [Fact]
    public void Moving_between_stage_groups_keeps_other_row_identity_and_distinct_comparisons()
    {
        var nav = new FileNavigation(); var a = File("a.cs", ComparisonKind.Worktree); var b = File("b.cs", ComparisonKind.Worktree);
        nav.Update("repo", [a, b], 1, false, a); var unchanged = nav.ShowFile(b);
        var staged = a with { Kind = ComparisonKind.Index };
        nav.Update("repo", [b, staged], 1, false, staged);
        Assert.Same(unchanged, nav.ShowFile(b)); Assert.Null(nav.ShowFile(a));
        nav.Update("repo", [a, b, staged], 1, false, a);
        Assert.NotSame(nav.ShowFile(a), nav.ShowFile(staged));
        var old = nav.ShowFile(a);
        nav.Update("other repo", [a], 1, false, a); Assert.NotSame(old, nav.ShowFile(a));
    }
}
