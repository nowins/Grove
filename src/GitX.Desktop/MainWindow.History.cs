using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private bool _syncingHistoryTree;
    private bool _historyPagingQueued;
    private void QueueHistoryPage()
    {
        if (_closed || _historyPagingQueued || !_model.CanAutoLoadHistory || !CommitList.IsEffectivelyVisible) return;
        _historyPagingQueued = true;
        // Scroll extents must reflect the appended rows before deciding whether
        // another page is needed. This also handles resize and tool re-docking.
        Dispatcher.UIThread.Post(async () =>
        {
            _historyPagingQueued = false;
            if (_closed || !_model.CanAutoLoadHistory || !CommitList.IsEffectivelyVisible) return;
            var scroll = CommitList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is null || scroll.Viewport.Height <= 0) return;
            var remaining = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
            if (remaining <= Math.Max(8 * 24, scroll.Viewport.Height / 2))
                await _model.LoadHistoryAsync(automatic: true);
        }, DispatcherPriority.Background);
    }
    private (string? Root, string? Tab, long Version, long Epoch)? _sourceFocusIntent;
    private long _loadedEditorVersion = -1;
    private void RequestSourceFocus()
    {
        _sourceFocusIntent = (_model.Root, _model.ActiveSourceTabId, _model.DocumentVersion, _inputEpoch);
        if (_loadedEditorVersion == _model.DocumentVersion) _ = FocusRequestedSourceAsync(_loadedEditorVersion);
    }
    private async Task FocusRequestedSourceAsync(long version)
    {
        if (_model.CurrentDocument is not { IsNotice: false }) return;
        _loadedEditorVersion = version;
        if (_sourceFocusIntent is not { } intent) return;
        bool Current() => !_closed && IsActive && _model.Root == intent.Root &&
            _model.ActiveSourceTabId == intent.Tab && _inputEpoch == intent.Epoch && _model.DocumentVersion == version && intent.Version == version;
        if (!Current()) { _sourceFocusIntent = null; return; }
        if (_model.CurrentDocument is not { IsNotice: false }) return;
        _sourceFocusIntent = null;
        // Wait for the actual source, then the native child's visibility/layout.
        // Focusing the loading notice cannot focus Monaco's later source model.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!Current()) return;
            FocusManager?.Focus(null); _webView?.Focus();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
        if (Current()) await InvokeEditorAsync("window.gitx.focus()");
    }
    private void NavigateHistoryBack() => ReturnToHistory(true);
    private void InitializeHistory()
    {
        _model.PropertyChanged += HistoryModelChanged;
        CommitList.AddHandler(PointerPressedEvent, HistoryPointerPressed, RoutingStrategies.Tunnel);
        CommitList.AddHandler(KeyDownEvent, HistoryMenuKeyDown, RoutingStrategies.Tunnel);
        CommitList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => QueueHistoryPage());
        CommitList.LayoutUpdated += (_, _) => QueueHistoryPage();
        HistoryFileList.ItemsSource = _historyNavigation.Rows;
        HistoryFileList.AddHandler(KeyDownEvent, HistoryFileKeyDown, RoutingStrategies.Tunnel);
        HistoryFileList.AddHandler(PointerPressedEvent, HistoryFilePointerPressed, RoutingStrategies.Tunnel);
        HistoryFileList.AddHandler(KeyDownEvent, HistoryFileMenuKeyDown, RoutingStrategies.Tunnel);
        HistoryFileList.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true) return;
            if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is FileNavigationRow { Entry: { } entry } && entry != _model.SelectedEntry)
                await _model.OpenEditorEntryAsync(entry);
        };
    }

    private void HistoryFiltersToggled(object? sender, RoutedEventArgs e) => UpdateHistoryFilterVisibility();

    private void UpdateHistoryFilterVisibility()
    {
        var expanded = HistoryFiltersToggle.IsChecked == true;
        HistoryAuthorFilterButton.IsVisible = expanded || _model.HasHistoryAuthorFilter;
        HistoryDateFilterButton.IsVisible = expanded || _model.HasHistoryDateFilter;
        HistoryPathFilterButton.IsVisible = expanded || _model.HasHistoryPathFilter;
        HistoryFiltersToggle.Classes.Set("active", expanded || _model.HasHistoryAuthorFilter || _model.HasHistoryDateFilter || _model.HasHistoryPathFilter);
    }

    private void HistoryModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasHistoryAuthorFilter) or nameof(MainViewModel.HasHistoryDateFilter) or nameof(MainViewModel.HasHistoryPathFilter))
            UpdateHistoryFilterVisibility();
        if (e.PropertyName is nameof(MainViewModel.HistoryBranches) or nameof(MainViewModel.HistoryReferenceKey))
        {
            _syncingHistoryTree = true;
            try
            {
                var nodes = HistoryBranchDescendants(_model.HistoryBranches);
                HistoryBranchTree.SelectedItem = nodes.FirstOrDefault(n => n.Key == (_model.HistoryReferenceKey ?? "all"));
            }
            finally { _syncingHistoryTree = false; }
        }
        if (e.PropertyName == nameof(MainViewModel.HistoryEntries)) { RefreshHistoryFiles(); UpdateNavigationLayout(); }
    }

    private async void HistoryBranchSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingHistoryTree || HistoryBranchTree.SelectedItem is not HistoryBranchNode node || node.Children.Count > 0) return;
        await _model.SelectHistoryReferenceAsync(node.Key == "all" ? null : node.Key);
    }

    private void HistoryBranchesToggled(object? sender, RoutedEventArgs e)
    {
        _historyPanePreferences.ToggleBranches(GitLogWorkspace.Bounds.Width);
        UpdateNavigationLayout();
    }

    private void HistoryFilesToggled(object? sender, RoutedEventArgs e)
    {
        _historyPanePreferences.ToggleFiles(GitLogWorkspace.Bounds.Width);
        UpdateNavigationLayout();
    }

    private async void HistoryFileSelected(object? sender, SelectionChangedEventArgs e)
    { if (!_syncingHistoryFiles && HistoryFileList.SelectedItems?.Count == 1 && HistoryFileList.SelectedItem is FileNavigationRow { Entry: { } entry }) await _model.OpenEditorEntryAsync(entry); }
    private void HistoryFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (HistoryFileList.SelectedItem is FileNavigationRow { CanExpand: true } row) ToggleHistoryDirectory(row);
        else if (HistoryFileList.SelectedItem is FileNavigationRow { IsFile: true }) { Activate(); RequestSourceFocus(); }
    }
    private void OpenHistoryDiff() { if (_model.IsHistory) FocusEditor(); }
    private void ReturnToHistory(bool focusCommits)
    {
        OpenTool(2, false, false);
        NativeKeyboardFocus.TryFocusControl(this, focusCommits ? CommitList : HistoryFileList);
    }
    private void HistoryBackClicked(object? sender, RoutedEventArgs e) => ReturnToHistory(true);
    private void HistoryOpenDiffClicked(object? sender, RoutedEventArgs e) => FocusEditor();

    private async void HistorySearchClicked(object? sender, RoutedEventArgs e) => await _model.ApplyHistoryQueryAsync();
    private async void HistorySearchKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await _model.ApplyHistoryQueryAsync(); } }
    private async void HistoryClearFilterClicked(object? sender, RoutedEventArgs e) => await _model.ApplyHistoryQueryAsync(clear: true);

    private async void HistoryCompareSelectedClicked(object? sender, RoutedEventArgs e)
    {
        var selected = CommitList.SelectedItems?.Cast<CommitEntry>().OrderBy(c => _model.Commits.ToList().IndexOf(c)).ToArray();
        if (selected?.Length != 2) return;
        await _model.CompareHistoryAsync(selected[1], selected[0]); ShowHistoryComparisonFiles();
    }
    private async void HistoryCompareWorktreeClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.SelectedCommit is not { } commit) return;
        await _model.CompareHistoryAsync(commit, null); ShowHistoryComparisonFiles();
    }
    private void ShowHistoryComparisonFiles()
    { OpenTool(2, false, false); _historyPanePreferences.ShowFiles(GitLogWorkspace.Bounds.Width); UpdateNavigationLayout(); }
}
