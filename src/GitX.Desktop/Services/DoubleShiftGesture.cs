namespace GitX.Desktop.Services;

// Two isolated press/release taps. Intervening keys, pointer input, focus loss
// and composition invalidate the gesture; a held/repeating Shift is not a tap.
internal sealed class DoubleShiftGesture
{
    private long? _firstRelease;
    private long _pressedAt;
    private bool _down, _clean;
    internal void Reset() { _firstRelease = null; _down = _clean = false; }
    internal void Down(bool shift, bool isolated, long now)
    {
        if (!shift || !isolated) { Reset(); return; }
        if (_down) { _clean = false; _firstRelease = null; return; }
        _down = _clean = true; _pressedAt = now;
        if (_firstRelease is { } first && (now < first || now - first > 500)) _firstRelease = null;
    }
    internal bool Up(bool shift, bool isolated, long now)
    {
        if (!shift || !isolated || !_down || !_clean || now < _pressedAt || now - _pressedAt > 300) { Reset(); return false; }
        _down = _clean = false;
        if (_firstRelease is { } first && now >= first && now - first <= 500) { Reset(); return true; }
        _firstRelease = now; return false;
    }
}
