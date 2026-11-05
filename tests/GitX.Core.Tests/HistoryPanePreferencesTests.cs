using Xunit;
using GitX.Desktop.ViewModels;

namespace GitX.Core.Tests;

public sealed class HistoryPanePreferencesTests
{
    [Theory]
    [InlineData(600, false, true, false, true)]
    [InlineData(760, false, true, false, false)]
    [InlineData(1199, false, true, false, false)]
    [InlineData(1200, true, true, true, false)]
    [InlineData(1600, true, true, true, false)]
    public void Unchosen_layout_uses_responsive_defaults(double width, bool branches, bool files, bool wide, bool stacked)
        => Assert.Equal((branches, files, wide, stacked), new HistoryPanePreferences().Resolve(width));

    [Fact]
    public void Explicit_hidden_panes_do_not_reopen_after_resize()
    {
        var preferences = new HistoryPanePreferences();
        preferences.ToggleBranches(1400); preferences.ToggleFiles(1400);
        foreach (var width in new[] { 900, 600, 1400, 1199, 1200, 1600 })
        {
            Assert.False(preferences.Resolve(width).Branches);
            Assert.False(preferences.Resolve(width).Files);
        }
    }

    [Fact]
    public void Compact_layout_temporarily_hides_branch_without_losing_choice()
    {
        var preferences = new HistoryPanePreferences();
        preferences.ToggleBranches(900);
        Assert.Equal((true, false, false, false), preferences.Resolve(900));
        Assert.Equal((false, false, false, true), preferences.Resolve(600));
        Assert.Equal((true, false, true, false), preferences.Resolve(1400));
        preferences.ToggleFiles(900);
        Assert.Equal((false, true, true, false), preferences.Resolve(1400));
    }
    [Fact]
    public void Comparison_reveals_files_without_toggling_them_off_on_repeated_requests()
    {
        var preferences = new HistoryPanePreferences();
        preferences.ToggleBranches(900);
        preferences.ShowFiles(900); preferences.ShowFiles(900);
        Assert.Equal((false, true, false, false), preferences.Resolve(900));
        Assert.Equal((false, true, true, false), preferences.Resolve(1400));
    }
}
