using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class RemoteQuickTargetTests
{
    private static ActionContext Context(string? upstream, bool detached = false) => new("root", "token", new('a', 40), "feature/local", "", false, false,
        detached ? [] : [new("feature/local", "refs/heads/feature/local", new('a', 40), false, true, upstream)], []);
    private static RemoteEntry Remote(string name) => new(name, name + ".git", name + ".git");

    [Fact]
    public void Tracking_uses_the_matching_remote_and_retains_branch_slashes()
    {
        var origin = Remote("origin"); var upstream = Remote("team/upstream");
        var target = RemoteQuickTarget.Resolve(Context("team/upstream/release/2026"), [origin, upstream]);
        Assert.Equal(upstream, target!.Remote);
        Assert.Equal("release/2026", target.Branch);
    }

    [Fact]
    public void Ambiguous_remote_prefixes_require_an_explicit_destination()
    {
        Assert.Null(RemoteQuickTarget.Resolve(Context("team/upstream/main"), [Remote("team"), Remote("team/upstream")]));
    }

    [Fact]
    public void Untracked_branch_can_name_one_destination_but_does_not_guess_between_remotes()
    {
        var origin = Remote("origin");
        Assert.Equal(new(origin, "feature/local"), RemoteQuickTarget.Resolve(Context(null), [origin]));
        Assert.Null(RemoteQuickTarget.Resolve(Context(null), [origin, Remote("backup")]));
        Assert.Null(RemoteQuickTarget.Resolve(Context("removed/main"), [origin]));
        Assert.Null(RemoteQuickTarget.Resolve(Context(null, detached: true), [origin]));
    }
}
