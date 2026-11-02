using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitX.Desktop.Services;

// Code-created controls use the same resources as XAML, including popup roots.
internal static class UiTypography
{
    public static FontFamily Family => (FontFamily)Application.Current!.FindResource("UiFontFamily")!;
    public static double Size => (double)Application.Current!.FindResource("UiFontSize")!;
    public static double SecondarySize => (double)Application.Current!.FindResource("UiSecondaryFontSize")!;
    public static double GitLogMetaSize => (double)Application.Current!.FindResource("GitLogMetaFontSize")!;
}
