using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly FileNavigation _navigation = new();
    private bool _syncingNavigation;
    private bool _navigationQueued;
    private bool _navigationRefresh;
    private string? _navigationRoot;
    private int _navigationMode = -1;
    private long _inputEpoch;

    private void InitializeNavigation()
    {
        AddFileFilterClear(FilterInput);
        FileList.ItemsSource = _navigation.Rows;
        FileList.AddHandler(KeyDownEvent, NavigationKeyDown, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerPressedEvent, FilePointerPressed, RoutingStrategies.Tunnel);
        FileList.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true) return;
            if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is FileNavigationRow { Entry: { } entry } && entry != _model.SelectedEntry)
                await _model.OpenEditorEntryAsync(entry);
        };
        CommitList.AddHandler(KeyDownEvent, (_, e) => NavigateListPage(CommitList, e), RoutingStrategies.Tunnel);
        _model.PropertyChanged += NavigationModelChanged;
        _model.NavigationChanged += QueueNavigationRefresh;
        AddHandler(PointerPressedEvent, (_, _) => _inputEpoch++, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => _inputEpoch++, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _inputEpoch++;
    }

    private void NavigationModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedEntry)) QueueNavigation();
    }

    private void QueueNavigation()
    {
        if (_navigationQueued || _closed) return;
        _navigationQueued = true;
        // Selection and entries are one presentation update, even when the model
        // publishes several notifications while reconciling a Git write.
        Dispatcher.UIThread.Post(() =>
        {
            _navigationQueued = false;
            if (_closed) return;
            if (_navigationRefresh) RefreshNavigation(); else SyncNavigationSelection();
        }, DispatcherPriority.Loaded);
    }

    private void QueueNavigationRefresh()
    {
        _navigationRefresh = true;
        QueueNavigation();
    }

    private void SyncNavigationSelection()
    {
        _syncingNavigation = true;
        try
        {
            if (_model.NavigationEntries(_leftTool).FirstOrDefault(entry => entry.Path == _toolSelections[_leftTool]) is { } entry)
            {
                var row = _navigation.ShowFile(entry);
                if (!ReferenceEquals(FileList.SelectedItem, row))
                {
                    FileList.SelectedItem = row;
                    if (row is not null) FileList.ScrollIntoView(row);
                }
            }
            else if (FileList.SelectedItem is FileNavigationRow { IsFile: true }) FileList.SelectedItem = null;
        }
        finally { _syncingNavigation = false; }
    }

    private void RefreshNavigation()
    {
        _navigationRefresh = false;
        var previous = FileList.SelectedItem as FileNavigationRow;
        var sameView = _navigationRoot == _model.Root && _navigationMode == _leftTool;
        var selectedContainer = FileList.ContainerFromIndex(FileList.SelectedIndex);
        var anchor = sameView ? selectedContainer : null;
        var anchorY = anchor?.TranslatePoint(default, FileList)?.Y;
        if (anchorY is null || anchorY < 0 || anchorY >= FileList.Bounds.Height)
        {
            anchor = sameView ? FileList.GetRealizedContainers().FirstOrDefault(c => c.TranslatePoint(default, FileList)?.Y >= 0) : null;
            anchorY = anchor?.TranslatePoint(default, FileList)?.Y;
        }
        var anchorRow = anchor?.DataContext as FileNavigationRow;
        var keepSelectedPosition = anchorRow is not null && ReferenceEquals(anchorRow, previous);
        var hadListFocus = FileList.IsKeyboardFocusWithin && !NativeKeyboardFocus.HasEmbeddedFocus(this);
        _syncingNavigation = true;
        try
        {
            var filter = _toolFilters[_leftTool];
            var entries = _model.NavigationEntries(_leftTool).Where(entry => string.IsNullOrWhiteSpace(filter) || entry.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            EmptyCommitHint.IsVisible = entries.Length == 0;
            EmptyCommitHint.Text = string.IsNullOrWhiteSpace(filter) ? "没有本地改动" : "没有匹配的文件";
            FileFilterButton.Classes.Set("active", !string.IsNullOrWhiteSpace(filter));
            var selectedEntry = entries.FirstOrDefault(entry => entry.Path == _toolSelections[_leftTool]);
            _navigation.Update(_model.Root, entries, _leftTool, !string.IsNullOrWhiteSpace(filter), selectedEntry,
                revealSelected: !(sameView && previous?.CanExpand == true));
            RefreshInclusion();
            FileList.SelectedItem = sameView && previous?.CanExpand == true ? _navigation.Rows.FirstOrDefault(r => r.Key == previous.Key)
                : selectedEntry is { } entry ? _navigation.ShowFile(entry) : null;
            _navigationRoot = _model.Root; _navigationMode = _leftTool;
            var target = keepSelectedPosition ? FileList.SelectedItem as FileNavigationRow
                : _navigation.Rows.FirstOrDefault(r => r.Key == anchorRow?.Key);
            if (target is not null && anchorY is { } y && FileList.Scroll is { } scroll)
            {
                FileList.ScrollIntoView(target); FileList.UpdateLayout();
                var index = _navigation.Rows.ToList().IndexOf(target);
                if (FileList.ContainerFromIndex(index)?.TranslatePoint(default, FileList) is { } position)
                    scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + position.Y - y));
            }
            if (hadListFocus)
            {
                // Preserve the visible anchor when the selected row was scrolled
                // off screen; focusing it would scroll back and undo restoration.
                var selected = FileList.ContainerFromIndex(FileList.SelectedIndex);
                var selectedY = selected?.TranslatePoint(default, FileList)?.Y;
                if (selectedY >= 0 && selectedY < FileList.Bounds.Height) selected!.Focus();
                else FileList.Focus();
            }
        }
        finally { _syncingNavigation = false; }
    }

    private async Task ReturnToNavigationAfterAsync(Func<Task> action)
    {
        var root = _model.Root; var mode = _model.Mode; var epoch = _inputEpoch;
        await action();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_closed || !IsActive || root != _model.Root || mode != _model.Mode || epoch != _inputEpoch
                || NativeKeyboardFocus.HasEmbeddedFocus(this)) return;
            RefreshNavigation();
            var list = FileList;
            var target = list.ContainerFromIndex(list.SelectedIndex) ?? list;
            NativeKeyboardFocus.TryFocusControl(this, target);
        }, DispatcherPriority.Loaded);
    }

    private async void NavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingNavigation)
        {
            // Tree selection controls navigation; selecting a group/folder must
            // not close the file the user is already reading.
            if (FileList.SelectedItem is FileNavigationRow { CanExpand: true }) return;
            if (FileList.SelectedItem is FileNavigationRow { Entry: { } entry })
            { _toolSelections[_leftTool] = entry.Path; await _model.OpenEditorEntryAsync(entry); }
        }
    }

    private void ToggleDirectory(FileNavigationRow row)
    {
        _syncingNavigation = true;
        try
        {
            _navigation.Toggle(row);
            RefreshInclusion();
            FileList.SelectedItem = _navigation.Rows.FirstOrDefault(r => r.Key == row.Key);
        }
        finally { _syncingNavigation = false; }
        FocusNavigationRow();
    }

    private void FocusNavigationRow()
    {
        FileList.ScrollIntoView(FileList.SelectedIndex);
        (FileList.ContainerFromIndex(FileList.SelectedIndex) ?? FileList).Focus();
    }

    private void DirectoryClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FileNavigationRow row)
        { if ((sender as Visual)?.GetVisualAncestors().Contains(_projectPanel) == true) ToggleProjectDirectory(row); else ToggleDirectory(row); e.Handled = true; }
    }

    private void NavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (_leftTool == 1 && (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        { e.Handled = true; ShowFileMenu(FileList.ContainerFromIndex(FileList.SelectedIndex) ?? FileList, false); return; }
        if (NavigateListPage(FileList, e)) return;
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            Dispatcher.UIThread.Post(() => { if (FileList.IsKeyboardFocusWithin) FileList.ScrollIntoView(FileList.SelectedIndex); }, DispatcherPriority.Loaded);
        if (FileList.SelectedItem is not FileNavigationRow row) return;
        if (_leftTool == 1 && e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && row.CanInclude)
        {
            e.Handled = true;
            if (row.IsGroup) ToggleVisibleChanges();
            else _model.SetFilesIncluded(FileList.SelectedItems?.OfType<FileNavigationRow>().Where(item => item.CanInclude && item.IsFile).Select(item => item.Path) ?? [row.Path]);
            return;
        }
        if (row.CanExpand && (e.Key is Key.Enter or Key.Space || e.Key == Key.Right && !row.IsExpanded || e.Key == Key.Left && row.IsExpanded))
        { e.Handled = true; ToggleDirectory(row); }
        else if (e.Key == Key.Left && _navigation.Parent(row) is { } parent)
        { e.Handled = true; FileList.SelectedItem = parent; FocusNavigationRow(); }
        else if (e.Key == Key.Right && row.IsDirectory && row.IsExpanded && FileList.SelectedIndex + 1 < _navigation.Rows.Count)
        { e.Handled = true; FileList.SelectedIndex++; FocusNavigationRow(); }
    }

    private static bool NavigateListPage(ListBox list, KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown) || e.KeyModifiers != KeyModifiers.None || list.ItemCount == 0) return false;
        // Avalonia's ScrollViewer handles these keys by scrolling alone. Move
        // selection by the visible page as well, so the displayed document agrees.
        var height = (list.ContainerFromIndex(list.SelectedIndex) ?? list.GetRealizedContainers().FirstOrDefault())?.Bounds.Height;
        if (height is not > 0) return false;
        var direction = e.Key == Key.PageDown ? 1 : -1;
        var step = Math.Max(1, (int)Math.Floor(list.Bounds.Height / height.Value) - 1);
        var index = list.SelectedIndex < 0 ? 0 : Math.Clamp(list.SelectedIndex + direction * step, 0, list.ItemCount - 1);
        var candidate = index;
        while (candidate >= 0 && candidate < list.ItemCount && list.Items[candidate] is FileNavigationRow { IsGroup: true }) candidate += direction;
        if (candidate < 0 || candidate >= list.ItemCount)
        {
            candidate = index - direction;
            while (candidate >= 0 && candidate < list.ItemCount && list.Items[candidate] is FileNavigationRow { IsGroup: true }) candidate -= direction;
        }
        if (candidate < 0 || candidate >= list.ItemCount) return false;
        index = candidate;
        list.SelectedIndex = index;
        list.ScrollIntoView(index);
        list.UpdateLayout();
        (list.ContainerFromIndex(index) ?? list).Focus(NavigationMethod.Directional);
        e.Handled = true;
        return true;
    }
}
