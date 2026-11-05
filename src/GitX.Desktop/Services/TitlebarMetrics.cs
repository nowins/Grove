namespace GitX.Desktop.Services;

internal readonly record struct TitlebarMetrics(double Height, double Left, double Right, double VerticalPadding, bool Compact)
{
    public static TitlebarMetrics ForPlatform(bool macOS, double nativeHeight, double? buttonsRight = null)
    {
        if (!macOS) return new(32, 42, 140, 2, false);
        // macOS owns the traffic lights. Use the framework's native titlebar
        // height in DIPs, rather than a Windows caption size or device pixels.
        var height = double.IsFinite(nativeHeight) && nativeHeight is >= 18 and <= 64 ? nativeHeight : 32;
        // Height does not predict the horizontal extent of native buttons.
        // Keep the previous conservative reservation only until native geometry
        // is available; add a small drag gap after the actual trailing edge.
        var left = buttonsRight is { } edge && double.IsFinite(edge) && edge is >= 0 and <= 256
            ? edge + 8 : height * 4;
        return new(height, left, 12, 0, true);
    }
}
