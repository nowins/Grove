using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly RecentFileStore _recentFiles = new(LocalDataDirectory);
    private RecentFilesWindow? _recentFilesWindow;
    private RecentFile? _lastRecentFile;
    private long _recentPickerGeneration;
    private Task _recentFileWrite = Task.CompletedTask;

    private void InitializeRecentFiles()
    {
        _model.RepositoryOpened += _ => { _lastRecentFile = null; _recentFilesWindow?.Close(); };
        _model.DocumentChanged += (_, document) =>
        {
            if (_model.Root is not { } root || document.IsNotice || document.IsDiff || !RecentFileStore.SafePath(document.Path)) return;
            var file = new RecentFile(root, document.Path, document.SourceRevision);
            if (file == _lastRecentFile || !RecentFileStore.SafeRevision(file.Revision)) return;
            _lastRecentFile = file;
            _recentFileWrite = RememberAsync(file);
        };
        async Task RememberAsync(RecentFile file)
        {
            try { await _recentFiles.RememberAsync(file); }
            catch (Exception ex) { if (!_closed && _model.Root == file.Root) _model.ReportError("最近文件未能保存：" + ex.Message); }
        }
        Closed += (_, _) => _recentFilesWindow?.Close();
    }

    private async Task OpenRecentFilesAsync()
    {
        if (_closed || _model.Root is not { } root) return;
        if (_recentFilesWindow is { IsDismissed: false } existing) { existing.FocusSearch(); return; }
        var generation = ++_recentPickerGeneration;
        _inputEpoch++; _sourceFocusIntent = null;
        var owner = InteractionWindow;
        var previous = owner.FocusManager?.GetFocusedElement() as Control;
        var editor = _webView?.IsFocused == true || NativeKeyboardFocus.HasEmbeddedFocus(owner);
        var window = new RecentFilesWindow(async file =>
        {
            if (_closed || _model.Root != root) return "仓库已变化，请重新打开最近文件。";
            if (await _model.OpenRecentFileAsync(root, file.Path, file.Revision)) return null;
            return "文件不存在或无法读取；当前文件保持原样。可移除这条记录。";
        }, async file => { await _recentFiles.ForgetAsync(file); });
        _recentFilesWindow = window;
        window.Closed += (_, _) => { if (_recentFilesWindow == window) _recentFilesWindow = null; };
        try
        {
            await _recentFileWrite;
            var files = await _recentFiles.ReadAsync(root);
            if (_closed || _model.Root != root) return;
            window.SetFiles(files); await window.ShowPickerAsync(owner);
        }
        catch (Exception ex) { if (!_closed && _model.Root == root) _model.ReportError("最近文件读取失败：" + ex.Message); }
        finally { if (_recentFilesWindow == window) _recentFilesWindow = null; }
        var version = _model.DocumentVersion; var epoch = _inputEpoch;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || generation != _recentPickerGeneration || _recentFilesWindow is not null || !window.RestoreFocusOnClose ||
                _model.Root != root || _model.DocumentVersion != version || _inputEpoch != epoch || !owner.IsActive) return;
            if (window.OpenedFile) { Activate(); RequestSourceFocus(); }
            else if (editor) { FocusManager?.Focus(null); _webView?.Focus(); _ = InvokeEditorAsync("window.gitx.focus()"); }
            else if (previous is { IsVisible: true, IsEnabled: true }) NativeKeyboardFocus.TryFocusControl(owner, previous);
        }, DispatcherPriority.Background);
    }
}
