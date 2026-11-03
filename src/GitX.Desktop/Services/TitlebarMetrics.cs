namespace GitX.Desktop.Services;

internal readonly record struct TitlebarMetrics(double Height, double Left, double Right, double VerticalPadding, bool Compact)
{
    public static TitlebarMetrics ForPlatform(bool macOS, double nativeHeight)
    {
        if (!macOS) return new(32, 42, 140, 2, false);
        // macOS owns the traffic lights. Use the framework's native titlebar
        // height in DIPs, rather than a Windows caption size or device pixels.
        var height = double.IsFinite(nativeHeight) && nativeHeight is >= 18 and <= 64 ? nativeHeight : 32;
        return new(height, height * 4, 12, 0, true);
    }
}
