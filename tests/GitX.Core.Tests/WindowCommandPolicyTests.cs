using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class WindowCommandPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void Repeated_system_minimize_and_restore_preserve_previous_state(int state)
    {
        var initial = (WindowCommandState)state;
        var policy = new WindowCommandPolicy(); policy.Observe(initial);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(policy.TryGetTarget(0xf027, initial, true, out var minimized)); Assert.Equal(WindowCommandState.Minimized, minimized);
            policy.Observe(minimized);
            Assert.True(policy.TryGetTarget(0xf020, minimized, true, out var repeated)); Assert.Equal(minimized, repeated);
            Assert.True(policy.TryGetTarget(0xf120, minimized, true, out var restored)); Assert.Equal(initial, restored);
            policy.Observe(restored);
        }
    }
    [Theory]
    [InlineData(0xf030)] // SC_MAXIMIZE
    [InlineData(0xf060)] // SC_CLOSE
    [InlineData(0xf100)] // SC_KEYMENU
    [InlineData(0)]
    public void Unrelated_system_commands_remain_owned_by_Windows(uint command)
    { Assert.False(new WindowCommandPolicy().TryGetTarget(command, WindowCommandState.Normal, true, out _)); }
    [Fact]
    public void Restore_from_maximized_and_non_minimizable_dialogs_use_default_handling()
    {
        var policy = new WindowCommandPolicy();
        Assert.False(policy.TryGetTarget(0xf120, WindowCommandState.Maximized, true, out _));
        Assert.False(policy.TryGetTarget(0xf020, WindowCommandState.Normal, false, out _));
    }
}
