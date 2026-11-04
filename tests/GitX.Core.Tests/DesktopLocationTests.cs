using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class DesktopLocationTests
{
    [Fact]
    public void File_and_directory_targets_keep_exact_names_and_use_the_right_terminal_directory()
    {
        using var repo=new TestRepository(); const string name="目录 空格/space '& $(no-command); [x].txt";
        repo.Write(name,"fixture");
        var file=DesktopLocations.Resolve(repo.Root,name,false);
        Assert.Equal(Path.GetFullPath(Path.Combine(repo.Root,name)),file.FullPath);
        Assert.Equal(Path.GetDirectoryName(file.FullPath),file.Directory);Assert.False(file.IsDirectory);
        var folder=DesktopLocations.Resolve(repo.Root,"目录 空格",true);
        Assert.Equal(file.Directory,folder.FullPath);Assert.Equal(folder.FullPath,folder.Directory);
        Assert.Equal(Path.GetFullPath(repo.Root),DesktopLocations.Resolve(repo.Root,"",true).Directory);
    }

    [Theory]
    [InlineData("../outside",false)]
    [InlineData("nested/../../outside",true)]
    [InlineData(".git/config",false)]
    [InlineData(".GIT",true)]
    [InlineData("missing",false)]
    [InlineData("missing",true)]
    public void Outside_metadata_and_missing_targets_are_rejected(string relative,bool directory)
    {
        using var repo=new TestRepository();
        Assert.Throws<IOException>(()=>DesktopLocations.Resolve(repo.Root,relative,directory));
        Assert.Throws<IOException>(()=>DesktopLocations.Resolve(repo.Root,Path.GetFullPath(repo.Root),true));
    }

    [Fact]
    public void Same_relative_name_resolves_only_in_its_captured_worktree_and_wrong_item_types_fail()
    {
        using var first=new TestRepository();using var next=new TestRepository();
        first.Write("same.txt","first");next.Write("same.txt","next");
        var a=DesktopLocations.Resolve(first.Root,"same.txt",false);var b=DesktopLocations.Resolve(next.Root,"same.txt",false);
        Assert.NotEqual(a.FullPath,b.FullPath);Assert.Equal("first",File.ReadAllText(a.FullPath));Assert.Equal("next",File.ReadAllText(b.FullPath));
        Assert.Throws<IOException>(()=>DesktopLocations.Resolve(first.Root,"same.txt",true));
        Assert.Throws<IOException>(()=>DesktopLocations.Resolve(first.Root,"",false));
        File.Delete(a.FullPath);Assert.Throws<IOException>(()=>DesktopLocations.Resolve(first.Root,"same.txt",false));
    }

    [Fact]
    public void Platform_launch_plans_never_embed_a_path_in_shell_code()
    {
        using var repo=new TestRepository();repo.Write("space '& $(no-command); [x].txt","data");
        var target=DesktopLocations.Resolve(repo.Root,"space '& $(no-command); [x].txt",false);
        var windows=DesktopLocations.Plan(target,DesktopLocationAction.Reveal,DesktopPlatform.Windows);
        Assert.True(windows.NativeWindowsSelection);Assert.Null(windows.Executable);Assert.Empty(windows.Arguments);
        var system=Path.Combine(repo.Container,"trusted-system");
        windows=DesktopLocations.Plan(target,DesktopLocationAction.Terminal,DesktopPlatform.Windows,system);
        Assert.Equal(Path.Combine(system,"WindowsPowerShell","v1.0","powershell.exe"),windows.Executable);
        Assert.Equal(new[]{"-NoLogo","-NoExit","-NoProfile"},windows.Arguments);Assert.Equal(target.Directory,windows.WorkingDirectory);
        var mac=DesktopLocations.Plan(target,DesktopLocationAction.Reveal,DesktopPlatform.MacOS);
        Assert.Equal("/usr/bin/open",mac.Executable);Assert.Equal(new[]{"-R",target.FullPath},mac.Arguments);
        mac=DesktopLocations.Plan(target,DesktopLocationAction.Terminal,DesktopPlatform.MacOS);
        Assert.Equal(new[]{"-a","/System/Applications/Utilities/Terminal.app",target.Directory},mac.Arguments);
        var other=DesktopLocations.Plan(target,DesktopLocationAction.Terminal,DesktopPlatform.Other);
        Assert.Equal("/usr/bin/x-terminal-emulator",other.Executable);Assert.Empty(other.Arguments);Assert.Equal(target.Directory,other.WorkingDirectory);
    }

    [Fact]
    public async Task Dispatcher_receives_an_exact_resolved_location_without_touching_file_bytes()
    {
        using var repo=new TestRepository();repo.Write("data.txt","unchanged");
        var target=DesktopLocations.Resolve(repo.Root,"data.txt",false);DesktopLaunchPlan? captured=null;
        var service=new DesktopLocations{Launch=plan=>{captured=plan;return Task.CompletedTask;}};
        await service.OpenAsync(target,DesktopLocationAction.Reveal);
        Assert.Equal(target,captured!.Location);Assert.Equal(DesktopLocationAction.Reveal,captured.Action);
        Assert.Equal("unchanged",File.ReadAllText(target.FullPath));
    }
}
