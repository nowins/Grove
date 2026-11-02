using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private long _historyFileMenuGeneration;
    private NavigationEntry[] SelectedHistoryFiles()
    {
        var rows = HistoryFileList.SelectedItems?.OfType<FileNavigationRow>().ToArray() ?? [];
        return _model.HistoryEntries.Where(entry => rows.Any(row => row.Entry == entry || row.IsDirectory &&
            (row.Path.Length == 0 || entry.Path.StartsWith(row.Path + "/", StringComparison.Ordinal)))).Distinct().ToArray();
    }
    private async void HistoryFilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(HistoryFileList).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not FileNavigationRow file) return;
        e.Handled = true;
        if (HistoryFileList.SelectedItems?.Contains(file) != true) HistoryFileList.SelectedItem = file;
        row.Focus(NavigationMethod.Pointer);
        await ShowHistoryFileMenuAsync(row, true);
    }
    private async void HistoryFileMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
        { e.Handled = true; await ShowHistoryFileMenuAsync(HistoryFileList.ContainerFromIndex(HistoryFileList.SelectedIndex) ?? HistoryFileList, false); }
    }
    private bool HistoryFileShortcut(KeyEventArgs e)
    {
        if (!HistoryFileList.IsKeyboardFocusWithin || TopLevel.GetTopLevel(HistoryFileList) is not Window { IsActive: true }) return false;
        if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control)
        { e.Handled = true; _ = OpenSelectedHistoryFilesAsync(false); return true; }
        if (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None)
        { e.Handled = true; _ = OpenSelectedHistoryFilesAsync(true); return true; }
        return false;
    }
    private async Task OpenSelectedHistoryFilesAsync(bool source)
    {
        if (_model.Root is not { } root) return;
        try { await OpenHistoryEntriesAsync(root, SelectedHistoryFiles(), source); }
        catch (Exception ex) { if (_model.Root == root) _model.ReportError(ex.Message); }
    }
    private async Task OpenHistoryEntriesAsync(string root, IEnumerable<NavigationEntry> entries, bool source = false)
    {
        var epoch = _inputEpoch;
        foreach (var entry in entries)
        {
            if (_closed || _model.Root != root || epoch != _inputEpoch) return;
            await _model.OpenEditorEntryAsync(source ? new(entry.Path, entry.Path, ComparisonKind.WorkingFile) : entry);
        }
        if (!_closed && _model.Root == root && epoch == _inputEpoch) { Activate(); RequestSourceFocus(); }
    }

    private async Task ShowHistoryFileMenuAsync(Control anchor, bool pointer)
    {
        if (_model.Root is not { } root) return;
        var entries = SelectedHistoryFiles(); if (entries.Length == 0) return;
        var generation = ++_historyFileMenuGeneration;
        bool Current() => !_closed && _model.Root == root && generation == _historyFileMenuGeneration;
        try
        {
            HistoryFileSelection? selection = null;
            if (entries.All(e => e.Kind == ComparisonKind.Commit && e.Commit == entries[0].Commit) && entries[0].Commit is { } sha)
                selection = await HistoryFileActions.ReadAsync(root, sha, entries.Select(e => e.Path));
            var local = await Task.Run(() => entries.All(e => File.Exists(RepositoryReader.SafeWorktreePath(root, e.Path))));
            if (!Current() || !entries.SequenceEqual(SelectedHistoryFiles())) return;
            var menu = new MenuFlyout();
            MenuItem Item(string label, Func<Task> run, bool enabled = true, string? icon = null, string? shortcut = null, string? tip = null)
            {
                var item = new MenuItem { Header = label, MinWidth = 300, IsEnabled = enabled, Icon = icon is null ? null : IconContent.Icon(icon) };
                if (shortcut is not null) item.InputGesture = KeyGesture.Parse(shortcut);
                if (tip is not null) ToolTip.SetTip(item, tip);
                item.Click += async (_, _) =>
                {
                    menu.Hide(); if (!Current()) return;
                    try { await run(); } catch (Exception ex) { if (_model.Root == root) _model.ReportError(ex.Message); }
                };
                menu.Items.Add(item); return item;
            }
            Item("显示差异", () => OpenHistoryEntriesAsync(root, entries), icon: "ChangesIcon", shortcut: "Ctrl+D");
            Item("在新窗口显示差异", async () =>
            {
                var entry = entries[0]; var epoch = _inputEpoch;
                using var reader = new RepositoryReader();
                var doc = await reader.DocumentAsync(new(root, entry.Path, entry.Kind, entry.Commit, entry.OldPath, DiffWhitespace.Trim, entry.BaseCommit));
                if (!Current() || epoch != _inputEpoch) return;
                if (doc.Diff is not { } diff) throw new IOException(doc.Text);
                new DiffWindow(entry.Path, diff.Old, diff.New).Show(this);
            }, entries.Length == 1, "ChangesIcon", tip: "独立窗口当前支持单个文件");
            async Task CompareLocal(bool before)
            {
                var kind = before ? ComparisonKind.CommitParentWorktree : ComparisonKind.CommitWorktree;
                await OpenHistoryEntriesAsync(root, selection!.Files.Select(f => new NavigationEntry(f.Path, f.Path, kind,
                    before ? f.OldPath : f.Path, selection.Commit)));
            }
            Item("与本地比较", () => CompareLocal(false), selection is not null);
            Item("将更改前版本与本地比较", () => CompareLocal(true), selection is not null);
            Item("打开本地源码", () => OpenHistoryEntriesAsync(root, entries, true), local, "FileIcon", "F4", "Grove 以只读方式打开本地源码");
            Item("打开仓库版本", async () =>
            {
                var epoch = _inputEpoch;
                foreach (var file in selection!.Files)
                {
                    if (!Current() || epoch != _inputEpoch) return;
                    await _model.OpenRevisionSourceAsync(file.AfterExists ? file.Path : file.OldPath, file.AfterExists ? selection.Commit : selection.Parent!);
                }
                if (Current() && epoch == _inputEpoch) { Activate(); RequestSourceFocus(); }
            }, selection is not null, tip: "只读打开此提交中的文件；删除项打开父提交中删除前的版本");
            menu.Items.Add(new Separator());
            var writable = selection is not null && !_model.IsWriting && !_model.HasOperation;
            Item("撤销所选更改…", () => WriteHistorySelectionAsync(selection!, HistoryFileOperation.Revert), writable, "UndoIcon");
            Item("拣选所选更改…", () => WriteHistorySelectionAsync(selection!, HistoryFileOperation.CherryPick), writable, "CherryPickIcon");
            const string deferred = "改写已有提交历史的操作暂未启用";
            Item("提取所选更改为独立提交…", () => Task.CompletedTask, false, tip: deferred);
            Item("丢弃所选更改", () => Task.CompletedTask, false, tip: deferred);
            menu.Items.Add(new Separator());
            Item("创建补丁…", async () =>
            {
                var patch = await HistoryFileActions.PatchAsync(selection!);
                if (!Current()) return;
                var target = await InteractionWindow.StorageProvider.SaveFilePickerAsync(new() { Title = "创建所选提交文件补丁", SuggestedFileName = selection!.Commit[..8] + ".patch", FileTypeChoices = [new("Git 补丁") { Patterns = ["*.patch"] }] });
                if (target is null) return;
                await using var stream = await target.OpenWriteAsync(); stream.SetLength(0);
                await stream.WriteAsync(patch);
            }, selection is not null, "SaveIcon");
            Item("从此修订获取…", () => WriteHistorySelectionAsync(selection!, HistoryFileOperation.GetRevision), writable, "DownIcon");
            Item("到此为止的文件历史", () => OpenFileHistoryAsync(new(root, entries[0].Path, selection!.Commit)), selection is not null && entries.Length == 1, "HistoryIcon");
            var parents = new MenuItem { Header = "显示与父提交的差异", MinWidth = 300, IsEnabled = selection?.Parents.Count > 0 };
            if (selection is not null)
                foreach (var (parent, index) in selection.Parents.Select((sha, index) => (sha, index)))
                {
                    var choice = new MenuItem { Header = $"父 {index + 1} · {parent[..8]}" };
                    choice.Click += async (_, _) =>
                    {
                        menu.Hide(); if (!Current()) return;
                        try { await OpenHistoryEntriesAsync(root, selection.Files.Select(f => new NavigationEntry(f.Path, f.Path, ComparisonKind.RevisionRange, f.Path, selection.Commit, BaseCommit: parent))); }
                        catch (Exception ex) { if (_model.Root == root) _model.ReportError(ex.Message); }
                    };
                    parents.Items.Add(choice);
                }
            menu.Items.Add(parents); menu.ShowAt(anchor, pointer);
        }
        catch (Exception ex) { if (Current()) _model.ReportError(ex.Message); }
    }

    private async Task WriteHistorySelectionAsync(HistoryFileSelection selection, HistoryFileOperation operation)
    {
        var root = _model.Root;
        if (_closed || root != selection.Root || _model.IsWriting) return;
        var plan = await HistoryFileActions.PrepareAsync(selection, operation);
        if (_closed || _model.Root != root || _model.IsWriting) return;
        var label = operation switch { HistoryFileOperation.Revert => "撤销所选更改", HistoryFileOperation.CherryPick => "拣选所选更改", _ => "从此修订获取" };
        var effect = operation switch
        {
            HistoryFileOperation.Revert => "将所选提交中这些文件的反向改动应用到工作区。",
            HistoryFileOperation.CherryPick => "将所选提交中这些文件的改动应用到工作区。",
            _ => "用此修订的文件版本替换工作区文件；此修订中已删除的文件会从工作区删除。"
        };
        var paths = string.Join('\n', selection.Files.Take(20).Select(f => f.Path)) + (selection.Files.Count > 20 ? $"\n…共 {selection.Files.Count} 个文件" : "");
        if (!await ConfirmDialog.ShowAsync(this, label, $"提交 {selection.Commit[..8]} · 相对 {(selection.Parent is { } parent ? parent[..8] : "空树")}\n{paths}\n\n{effect}\n保留 HEAD 和索引，不自动提交。原文件先备份，可用 Git → 撤销丢弃恢复。", "备份并执行")) return;
        if (_closed || _model.Root != root || _model.IsWriting) return;
        await _model.WriteHistoryFilesAsync(plan);
    }
}
