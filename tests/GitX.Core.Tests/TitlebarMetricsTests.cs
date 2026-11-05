using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class TitlebarMetricsTests
{
    [Theory]
    [InlineData(22)]
    [InlineData(28)]
    [InlineData(36)]
    public void Native_height_is_used_with_conservative_reservation_until_geometry_is_available(double nativeHeight)
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
    [Theory]
    [InlineData(22, 60)]
    [InlineData(28, 60)]
    [InlineData(36, 72)]
    [InlineData(64, 94)]
    [InlineData(0, 60)]
    public void Native_button_edge_controls_reservation_independently_of_height(double height, double edge)
    {
        var metrics = TitlebarMetrics.ForPlatform(true, height, edge);
        Assert.Equal(edge + 8, metrics.Left);
        Assert.Equal(12, metrics.Right);
    }

    [Fact]
    public void Hidden_fullscreen_buttons_leave_only_the_drag_gap()
    { Assert.Equal(8, TitlebarMetrics.ForPlatform(true, 0, 0).Left); }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1000)]
    public void Invalid_native_geometry_preserves_safe_fallback(double edge)
    { Assert.Equal(112, TitlebarMetrics.ForPlatform(true, 28, edge).Left); }

    [Fact]
    public void Mac_geometry_does_not_change_Windows_reservations()
    { Assert.Equal(new TitlebarMetrics(32, 42, 140, 2, false), TitlebarMetrics.ForPlatform(false, 22, 60)); }

    [Fact]
    public void Windows_row_matches_managed_caption_and_keeps_space_for_system_buttons()
    { Assert.Equal(new TitlebarMetrics(32, 42, 140, 2, false), TitlebarMetrics.ForPlatform(false, 22)); }
}
