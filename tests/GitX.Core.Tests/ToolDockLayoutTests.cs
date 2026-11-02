using System.Text.Json;
using GitX.Desktop.ViewModels;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ToolDockLayoutTests
{
    [Fact]
    public void Tools_can_move_independently_and_only_replace_the_active_tool_at_the_target_edge()
    {
        var layout = new ToolDockLayout(); layout.Show(ToolId.GitLog);
        layout.Dock(ToolId.Commit, ToolEdge.Right);
        Assert.True(layout[ToolId.Project].Visible); Assert.True(layout[ToolId.GitLog].Visible); Assert.True(layout[ToolId.Commit].Visible);
        layout.Dock(ToolId.Project, ToolEdge.Right);
        Assert.False(layout[ToolId.Commit].Visible); Assert.True(layout[ToolId.GitLog].Visible);
        Assert.Equal(ToolEdge.Right, layout[ToolId.Commit].Edge);
    }
    [Fact]
    public void Hiding_a_float_keeps_its_mode_bounds_and_return_anchor()
    {
        var layout = new ToolDockLayout(); layout.Dock(ToolId.Commit, ToolEdge.Right);
        layout.Float(ToolId.Commit, -900, 120); layout.SetBounds(ToolId.Commit, -850, 140, 480, 660);
        layout.Hide(ToolId.Commit); layout.Show(ToolId.Commit);
        Assert.Equal(new ToolPlacement(ToolId.Commit, ToolEdge.Right, true, true, 0, -850, 140, 480, 660), layout[ToolId.Commit]);
        layout.Dock(ToolId.Commit, layout[ToolId.Commit].Edge); Assert.False(layout[ToolId.Commit].Floating);
        Assert.Equal(-850, layout[ToolId.Commit].X);
    }
    [Fact]
    public void Floating_tools_can_coexist_and_order_can_be_changed_without_losing_their_geometry()
    {
        var layout = new ToolDockLayout(); layout.Float(ToolId.Project); layout.Float(ToolId.Commit);
        layout.Show(ToolId.Project); Assert.True(layout[ToolId.Commit].Visible);
        layout.Dock(ToolId.Project, ToolEdge.Left, 1); layout.Dock(ToolId.Commit, ToolEdge.Left, 0);
        Assert.Equal(new[] { ToolId.Commit, ToolId.Project }, layout.Tools.Where(t => t.Edge == ToolEdge.Left).Select(t => t.Id));
        Assert.Equal(420, layout[ToolId.Project].Width);
    }
    [Theory]
    [InlineData(0, 300, ToolEdge.Left)]
    [InlineData(999, 300, ToolEdge.Right)]
    [InlineData(500, 0, ToolEdge.Top)]
    [InlineData(500, 599, ToolEdge.Bottom)]
    public void Drag_edges_use_local_geometry_and_outside_or_center_drops_float(double x, double y, ToolEdge expected)
    {
        Assert.Equal(expected, ToolDockLayout.DropEdge(x, y, 1000, 600));
        Assert.Null(ToolDockLayout.DropEdge(-1, 300, 1000, 600));
        Assert.Null(ToolDockLayout.DropEdge(1001, 300, 1000, 600));
        Assert.Null(ToolDockLayout.DropEdge(500, 300, 1000, 600));
    }
    [Fact]
    public void Invalid_or_duplicate_layout_entries_do_not_hide_the_workspace_or_create_invalid_windows()
    {
        var layout = new ToolDockLayout();
        layout.Restore(new(1, [new(ToolId.Project, ToolEdge.Right, Visible: true), new(ToolId.Project, ToolEdge.Left),
            new(ToolId.Commit, ToolEdge.Right, Visible: true), new(ToolId.GitLog, (ToolEdge)200),
            new(ToolId.FileHistory, ToolEdge.Top, Width: double.NaN)]));
        Assert.Equal(ToolEdge.Right, layout[ToolId.Project].Edge); Assert.True(layout[ToolId.Project].Visible);
        Assert.False(layout[ToolId.Commit].Visible); Assert.Equal(ToolEdge.Bottom, layout[ToolId.GitLog].Edge);
        Assert.Equal(ToolEdge.Bottom, layout[ToolId.FileHistory].Edge);
        layout.Restore(new(99, [])); Assert.True(layout[ToolId.Project].Visible); Assert.Equal(ToolEdge.Left, layout[ToolId.Project].Edge);
    }
    [Fact]
    public async Task Layout_save_is_atomic_and_restart_restores_dock_modes_and_float_geometry()
    {
        using var repo = new TestRepository(); var path = Path.Combine(repo.Root, "layout", "tools.json");
        var store = new ToolLayoutStore(path); Assert.Null(await store.ReadAsync());
        var layout = new ToolDockLayout(); layout.Dock(ToolId.GitLog, ToolEdge.Top); layout.Float(ToolId.Commit, 123, 234);
        await store.WriteAsync(new(1, layout.Tools.ToArray(), Bottom: 420));
        var restored = new ToolDockLayout(); var snapshot = await store.ReadAsync(); restored.Restore(snapshot!);
        Assert.Equal(layout.Tools, restored.Tools); Assert.Equal(420, snapshot!.Bottom);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        var saves = Enumerable.Range(0, 20).Select(index => store.WriteAsync(new(1, layout.Tools.ToArray(), Bottom: 300 + index))).ToArray();
        await Task.WhenAll(saves); Assert.Equal(319, (await store.ReadAsync())!.Bottom);
        await File.WriteAllTextAsync(path, "broken json"); Assert.Null(await store.ReadAsync());
    }
    [Fact]
    public void Float_bounds_fit_the_target_work_area_in_pixels_including_negative_origins_and_scaling()
    {
        var tool = new ToolPlacement(ToolId.GitLog, ToolEdge.Bottom, X: 1800, Y: 800, Width: 1100, Height: 600);
        var fitted = ToolDockLayout.FitFloat(tool, 0, 0, 1920, 1080, 1.5);
        Assert.Equal(270, fitted.X); Assert.Equal(180, fitted.Y);
        Assert.True(fitted.X + fitted.Width * 1.5 <= 1920); Assert.True(fitted.Y + fitted.Height * 1.5 <= 1080);
        var missingMonitor = ToolDockLayout.FitFloat(tool with { X = -9000, Y = -5000, Width = 9000 }, -1920, 0, 1920, 1080, 1);
        Assert.Equal(-1920, missingMonitor.X); Assert.Equal(0, missingMonitor.Y); Assert.Equal(1920, missingMonitor.Width);
    }

    [Fact]
    public void Independent_window_mode_survives_hide_and_restore_but_docking_clears_it()
    {
        var layout = new ToolDockLayout(); layout.Float(ToolId.Commit, independent: true);
        layout.Hide(ToolId.Commit); layout.Show(ToolId.Commit); Assert.True(layout[ToolId.Commit].Independent);
        var restored = new ToolDockLayout(); restored.Restore(new(1, layout.Tools.ToArray())); Assert.True(restored[ToolId.Commit].Independent);
        restored.Dock(ToolId.Commit, ToolEdge.Left); Assert.False(restored[ToolId.Commit].Independent);
    }

    [Fact]
    public void Reset_recovers_a_usable_docked_project_without_destroying_other_tool_definitions()
    {
        var layout = new ToolDockLayout(); foreach (var id in Enum.GetValues<ToolId>()) layout.Float(id, 3000, 2000);
        layout.Reset(); Assert.Equal(6, layout.Tools.Count); Assert.Single(layout.Tools, t => t.Visible);
        Assert.True(layout[ToolId.Project].Visible); Assert.All(layout.Tools, tool => Assert.False(tool.Floating));
    }
}
