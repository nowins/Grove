using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class ShellActionSearchTests
{
    [Fact]
    public void EmptyQueryListsOnlySafeAvailableNavigationCommands()
    {
        var commands = ShellCommands.SearchActions("/", false, id => id != ShellCommandId.GoToLine);
        Assert.NotEmpty(commands);
        Assert.Contains(commands, c => c.Id == ShellCommandId.FindFile);
        foreach (var id in new[] { ShellCommandId.GoToLine, ShellCommandId.Stage, ShellCommandId.Unstage,
            ShellCommandId.SaveFile, ShellCommandId.Amend, ShellCommandId.Sync, ShellCommandId.CloseWindow,
            ShellCommandId.OpenRepository, ShellCommandId.QuickSearch })
            Assert.DoesNotContain(commands, c => c.Id == id);
        Assert.Equal(commands.Count, commands.Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("/findfile", "FindFile")]
    [InlineData("FindFile", "FindFile")]
    [InlineData("查找 文件", "FindFile")]
    [InlineData("搜索 ctrl+shift+n", "FindFile")]
    [InlineData("/跳转 行", "GoToLine")]
    public void MatchesLabelsIdsAndAllQueryWords(string query, string expected)
    {
        Assert.Equal(expected, Assert.Single(ShellCommands.SearchActions(query, false, _ => true)).Id.ToString());
    }

    [Fact]
    public void MatchesOnlyCurrentPlatformShortcut()
    {
        Assert.Equal(ShellCommandId.FindFile, Assert.Single(ShellCommands.SearchActions("Meta+Shift+O", true, _ => true)).Id);
        Assert.Empty(ShellCommands.SearchActions("Meta+Shift+O", false, _ => true));
    }

    [Fact]
    public void RechecksAvailabilityForEverySearch()
    {
        var allowed = true;
        bool CanRun(ShellCommandId _) => allowed;
        Assert.NotEmpty(ShellCommands.SearchActions("", false, CanRun));
        allowed = false;
        Assert.Empty(ShellCommands.SearchActions("", false, CanRun));
        Assert.Empty(ShellCommands.SearchActions("does-not-exist", false, _ => true));
    }
}
