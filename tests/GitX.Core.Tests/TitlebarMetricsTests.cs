using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class TitlebarMetricsTests
{
    [Theory]
    [InlineData(22)]
    [InlineData(28)]
    [InlineData(36)]
    public void Native_mac_titlebar_height_controls_row_and_left_reservation(double nativeHeight)
    {
        var metrics = TitlebarMetrics.ForPlatform(true, nativeHeight);
        Assert.Equal(nativeHeight, metrics.Height); Assert.Equal(nativeHeight * 4, metrics.Left);
        Assert.Equal(12, metrics.Right); Assert.Equal(0, metrics.VerticalPadding); Assert.True(metrics.Compact);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1000)]
    public void Missing_or_invalid_native_height_has_a_bounded_fallback(double nativeHeight)
    { Assert.Equal(new TitlebarMetrics(32, 128, 12, 0, true), TitlebarMetrics.ForPlatform(true, nativeHeight)); }
    [Fact]
    public void Windows_row_matches_managed_caption_and_keeps_space_for_system_buttons()
    { Assert.Equal(new TitlebarMetrics(32, 42, 140, 2, false), TitlebarMetrics.ForPlatform(false, 22)); }
}
