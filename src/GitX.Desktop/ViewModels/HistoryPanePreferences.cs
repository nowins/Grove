namespace GitX.Desktop.ViewModels;

// Window-session choices survive responsive layout changes. A compact layout may
// temporarily hide the branch pane without changing the user's stored choice.
public sealed class HistoryPanePreferences
{
    private bool? _branches, _files;
    public (bool Branches, bool Files, bool Wide, bool Stacked) Resolve(double width)
    {
        var wide = width >= 1200;
        var stacked = width is > 0 and < 760;
        return (!stacked && (_branches ?? wide), _files ?? true, wide, stacked);
    }

    public void ToggleBranches(double width)
    {
        var state = Resolve(width);
        _branches = !state.Branches;
        if (_branches == true && !state.Wide) _files = false;
    }

    public void ShowFiles(double width)
    {
        _files = true;
        if (!Resolve(width).Wide) _branches = false;
    }

    public void ToggleFiles(double width)
    {
        var state = Resolve(width);
        _files = !state.Files;
        if (_files == true && !state.Wide) _branches = false;
    }
}
