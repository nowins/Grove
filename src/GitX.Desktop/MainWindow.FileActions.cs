using System.Text;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private void FilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FileList).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not FileNavigationRow { IsFile: true } file) return;
        e.Handled = true;
        if (FileList.SelectedItems?.Contains(file) != true) FileList.SelectedItem = file;
        row.Focus(NavigationMethod.Pointer);
        if (_leftTool == 0 && _model.Root is { } root)
        {
            var menu = new MenuFlyout();
            var history = new MenuItem { Header = "文件历史", Icon = IconContent.Icon("HistoryIcon") };
            history.Click += async (_, _) => { menu.Hide(); await OpenFileHistoryAsync(new(root, file.Path)); };
            menu.Items.Add(history); menu.ShowAt(row, true); return;
        }
        if (_leftTool != 1) return;
        ShowFileMenu(row, true);
    }

    private void ShowFileMenu(Control anchor, bool atPointer)
    {
        if (_model.Root is not { } root || _leftTool != 1) return;
        var selected = FileList.SelectedItems?.OfType<FileNavigationRow>().Where(row => row.IsFile).Select(row => row.Entry!).ToArray() ?? [];
        if (selected.Length == 0) return;
        var menu = new MenuFlyout();
        MenuItem Item(string text, Func<Task> action, bool enabled = true, string? icon = null)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled, Icon = icon is null ? null : IconContent.Icon(icon) };
            item.Click += async (_, _) =>
            {
                menu.Hide(); if (_closed || _model.Root != root || _leftTool != 1) return;
                try { await action(); } catch (Exception ex) { _model.ReportError(ex.Message); }
            };
            menu.Items.Add(item);
            return item;
        }
        Item($"包含所选 {selected.Length} 个文件", () => { _model.SetFilesIncluded(selected.Select(entry => entry.Path), true); return Task.CompletedTask; }, !_model.IsWriting, "CheckIcon");
        Item("取消包含所选文件", () => { _model.SetFilesIncluded(selected.Select(entry => entry.Path), false); return Task.CompletedTask; }, !_model.IsWriting, "MinusIcon");
        menu.Items.Add(new Separator());
        Item("跳转到源文件", async () =>
        {
            var path = selected[0].Path; SwitchView(0);
            await _model.OpenEditorEntryAsync(new(path, path, ComparisonKind.WorkingFile));
            UpdateNavigationLayout(); FocusEditor();
        }, selected.Length == 1 && !selected[0].IsDeleted, "FileIcon");
        Item("在独立窗口查看差异", async () =>
        {
            var entry = selected[0]; using var reader = new RepositoryReader();
            var document = await reader.DocumentAsync(new(root, entry.Path, ComparisonKind.LocalChanges, OldPath: entry.OldPath, Whitespace: DiffWhitespace.Trim));
            if (_closed || _model.Root != root) return;
            if (document.Diff is not { } diff) throw new IOException("此文件没有文本差异可在独立窗口显示。");
            new DiffWindow(entry.Path, diff.Old, diff.New).Show(this);
        }, selected.Length == 1 && !selected[0].IsConflict, "ChangesIcon");
        Item("文件历史", () => OpenFileHistoryAsync(new(root, selected[0].Path)), selected.Length == 1, "HistoryIcon");
        bool CanDiscard() => selected.Length == 1 && _model.Root == root && _model.SelectedEntry?.Path == selected[0].Path && _model.CanStageFile;
        var discard = Item("回退文件改动…", async () =>
        {
            if (CanDiscard() && _model.CurrentWrite() is { } context) await DiscardFileAsync(context);
        }, CanDiscard(), "UndoIcon");
        PropertyChangedEventHandler refresh = (_, _) => discard.IsEnabled = CanDiscard();
        _model.PropertyChanged += refresh;
        menu.Closed += (_, _) => _model.PropertyChanged -= refresh;
        menu.Items.Add(new Separator());
        Item("复制相对路径", async () => await (GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。")).SetTextAsync(string.Join('\n', selected.Select(entry => entry.Path))));
        var paths = selected.SelectMany(entry => entry.OldPath is { } old && old != entry.Path ? new[] { old, entry.Path } : [entry.Path]).ToArray();
        Item("复制补丁", async () =>
        {
            var patch = await RepositoryPatch.ReadAsync(root, paths);
            await (GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。")).SetTextAsync(patch);
        }, selected.All(entry => !entry.IsConflict), "ChangesIcon");
        Item("保存补丁…", async () =>
        {
            var patch = await RepositoryPatch.ReadAsync(root, paths);
            var target = await StorageProvider.SaveFilePickerAsync(new() { Title = "保存所选文件补丁", SuggestedFileName = "changes.patch", FileTypeChoices = [new("Git 补丁") { Patterns = ["*.patch"] }] });
            if (target is null) return;
            await using var stream = await target.OpenWriteAsync(); stream.SetLength(0);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(patch));
        }, selected.All(entry => !entry.IsConflict), "SaveIcon");
        menu.ShowAt(anchor, atPointer);
    }
}
