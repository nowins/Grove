namespace GitX.Desktop.Services;

internal enum WindowCommandState { Normal, Minimized, Maximized, FullScreen }

internal sealed class WindowCommandPolicy
{
    private WindowCommandState _restore = WindowCommandState.Normal;
    public void Observe(WindowCommandState state) { if (state != WindowCommandState.Minimized) _restore = state; }
    public bool TryGetTarget(uint command, WindowCommandState current, bool canMinimize, out WindowCommandState target)
    {
        target = current;
        if (!canMinimize) return false;
        switch (command & 0xfff0)
        {
            case 0xf020: // SC_MINIMIZE; low four bits are reserved by Windows.
                Observe(current); target = WindowCommandState.Minimized; return true;
            case 0xf120 when current == WindowCommandState.Minimized:
                target = _restore; return true;
            default: return false;
        }
    }
}
