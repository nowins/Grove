using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class DoubleShiftTests
{
    private static bool Tap(DoubleShiftGesture gesture, long time, long duration = 40, bool allowed = true)
    { gesture.Down(true, allowed, time); return gesture.Up(true, allowed, time + duration); }

    [Fact]
    public void Two_clean_taps_trigger_once_and_a_third_starts_a_new_pair()
    {
        var gesture = new DoubleShiftGesture();
        Assert.False(Tap(gesture, 0)); Assert.True(Tap(gesture, 100));
        Assert.False(Tap(gesture, 200)); Assert.True(Tap(gesture, 300));
    }
    [Fact]
    public void Slow_taps_expire_and_a_released_hold_is_not_the_first_tap()
    {
        var gesture = new DoubleShiftGesture();
        Assert.False(Tap(gesture, 0)); Assert.False(Tap(gesture, 600)); Assert.True(Tap(gesture, 700));
        Assert.False(Tap(gesture, 1000, 400)); Assert.False(Tap(gesture, 1450)); Assert.True(Tap(gesture, 1550));
    }
    [Fact]
    public void Auto_repeat_and_unmatched_keyup_never_form_a_pair()
    {
        var gesture = new DoubleShiftGesture(); Assert.False(gesture.Up(true, true, 0));
        gesture.Down(true, true, 10); gesture.Down(true, true, 20); Assert.False(gesture.Up(true, true, 50));
        Assert.False(Tap(gesture, 70)); Assert.True(Tap(gesture, 170));
    }
    [Theory]
    [InlineData("key")]
    [InlineData("chord")]
    [InlineData("reset")]
    [InlineData("composition")]
    public void Typing_chords_pointer_focus_and_composition_interrupt_a_pair(string action)
    {
        var gesture = new DoubleShiftGesture(); Assert.False(Tap(gesture, 0));
        switch (action)
        {
            case "key": gesture.Down(false, true, 60); gesture.Up(false, true, 80); break;
            case "chord": Tap(gesture, 60, allowed: false); break;
            case "composition": gesture.Down(true, false, 60); gesture.Up(true, true, 80); break;
            default: gesture.Reset(); break;
        }
        Assert.False(Tap(gesture, 150)); Assert.True(Tap(gesture, 250));
    }
    [Fact]
    public void Complete_release_must_be_within_the_window_and_monotonic()
    {
        var gesture = new DoubleShiftGesture(); Assert.False(Tap(gesture, 0)); Assert.True(Tap(gesture, 500));
        Assert.False(Tap(gesture, 1000)); Assert.False(Tap(gesture, 1530));
        gesture.Reset(); Assert.False(Tap(gesture, 100)); Assert.False(Tap(gesture, 0));
    }
}
