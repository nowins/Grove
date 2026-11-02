using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly FileNavigation _historyNavigation = new();
    private bool _syncingHistoryFiles, _historyFlatFiles, _historyCompactDirectories = true;

    private void RefreshHistoryFiles()
    {
        ++_historyFileMenuGeneration;
        var previous = HistoryFileList.SelectedItem as FileNavigationRow;
        _syncingHistoryFiles = true;
        try
        {
            _historyNavigation.Update(_model.Root, _model.HistoryEntries, _historyFlatFiles ? 3 : 2, false, null,
                compactDirectories: _historyCompactDirectories);
            HistoryFileList.SelectedItem = _historyNavigation.Rows.FirstOrDefault(r => r.Key == previous?.Key);
        }
        finally { _syncingHistoryFiles = false; }
    }

    private void ToggleHistoryDirectory(FileNavigationRow row)
    {
        _syncingHistoryFiles = true;
        try { _historyNavigation.Toggle(row); HistoryFileList.SelectedItem = _historyNavigation.Rows.FirstOrDefault(r => r.Key == row.Key); }
        finally { _syncingHistoryFiles = false; }
        FocusHistoryFileRow();
    }
    private void FocusHistoryFileRow()
    {
        HistoryFileList.ScrollIntoView(HistoryFileList.SelectedIndex);
        (HistoryFileList.ContainerFromIndex(HistoryFileList.SelectedIndex) ?? HistoryFileList).Focus();
    }
    private void HistoryDirectoryClicked(object? sender, RoutedEventArgs e)
    { if ((sender as Control)?.DataContext is FileNavigationRow row) { ToggleHistoryDirectory(row); e.Handled = true; } }
    private void HistoryFileKeyDown(object? sender, KeyEventArgs e)
    {
        if (NavigateListPage(HistoryFileList, e)) return;
        if (HistoryFileList.SelectedItem is not FileNavigationRow row) return;
        if (row.CanExpand && (e.Key is Key.Enter or Key.Space || e.Key == Key.Right && !row.IsExpanded || e.Key == Key.Left && row.IsExpanded))
        { e.Handled = true; ToggleHistoryDirectory(row); }
        else if (e.Key == Key.Left && _historyNavigation.Parent(row) is { } parent)
        { e.Handled = true; HistoryFileList.SelectedItem = parent; FocusHistoryFileRow(); }
        else if (e.Key == Key.Right && row.IsDirectory && row.IsExpanded && HistoryFileList.SelectedIndex + 1 < _historyNavigation.Rows.Count)
        { e.Handled = true; HistoryFileList.SelectedIndex++; FocusHistoryFileRow(); }
        else if (e.Key == Key.Enter && row.Entry is not null) { e.Handled = true; Activate(); RequestSourceFocus(); }
    }
    private void HistoryFileSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || ToggleShellPopup(anchor)) return;
        var panel = new StackPanel { Width = 210, Spacing = 2 };
        void Choice(string label, bool selected, Action action)
        { panel.Children.Add(ShellAction(label, selected ? "CheckIcon" : null, () => { CloseShellPopup(); action(); })); }
        Choice("目录树", !_historyFlatFiles, () => { _historyFlatFiles = false; RefreshHistoryFiles(); });
        Choice("平铺路径", _historyFlatFiles, () => { _historyFlatFiles = true; RefreshHistoryFiles(); });
        panel.Children.Add(new Separator());
        Choice("压缩单子目录", _historyCompactDirectories, () => { _historyCompactDirectories = !_historyCompactDirectories; RefreshHistoryFiles(); });
        void Expand(bool expand)
        {
            _syncingHistoryFiles = true;
            try { _historyNavigation.ExpandAll(expand); HistoryFileList.SelectedItem = null; }
            finally { _syncingHistoryFiles = false; }
        }
        Choice("全部展开", false, () => Expand(true)); Choice("全部收起", false, () => Expand(false));
        ShowHistoryPopup(panel, anchor);
    }
}
