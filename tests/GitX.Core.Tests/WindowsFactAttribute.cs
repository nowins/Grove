using Xunit;

namespace GitX.Core.Tests;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    { if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI CurrentUser; not verified on this platform."; }
}
