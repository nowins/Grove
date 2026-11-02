using Avalonia.Controls;
using GitX.Desktop.Controls;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private FileHistoryPanel? _fileHistoryPanel;
    private Task OpenSourceHistoryAsync(long version)
    {
        if (_closed || !_ready || version != _publishedVersion || version != _model.DocumentVersion ||
            _model.Mode != 0 || _model.Root is not { } root || _model.CurrentDocument is not { IsNotice: false } document)
            return Task.CompletedTask;
        return OpenFileHistoryAsync(new(root, document.Path));
    }
    private Task OpenFileHistoryAsync(FileHistoryTarget target)
    {
        if (_closed || _model.Root != target.Root) return Task.CompletedTask;
        if (_fileHistoryPanel?.Target == target) { _dock?.Show(ToolId.FileHistory); return Task.CompletedTask; }
        CloseFileHistory(false);
        var panel = _fileHistoryPanel = new FileHistoryPanel(this, target);
        panel.ToolRequested += tool => { if (_fileHistoryPanel == panel) OpenTool(tool, false, true); };
        panel.SearchRequested += (filesOnly, quick) =>
        {
            if (_fileHistoryPanel == panel) _ = OpenCodeSearchAsync(filesOnly, quick, () =>
            { if (_fileHistoryPanel == panel && _fileHistoryActive) panel.FocusDiff(); });
        };
        panel.CloseRequested += () => { if (_fileHistoryPanel == panel) CloseFileHistory(); };
        panel.HideRequested += () => { if (_fileHistoryPanel == panel) { _dock?.Hide(ToolId.FileHistory); FocusEditor(); } };
        panel.ResetLayoutRequested += () => { if (_fileHistoryPanel == panel && _dock is not null) _ = _dock.ResetAsync(); };
        FileHistoryHost.Content = panel;
        _dock?.SetAvailable(ToolId.FileHistory, true, $"历史: {Path.GetFileName(target.Path)}" + (target.UntilRevision is { } until ? $" @{until[..Math.Min(8, until.Length)]}" : ""));
        _dock?.Show(ToolId.FileHistory); UpdateNavigationLayout(); return Task.CompletedTask;
    }
    private void CloseFileHistory(bool focusSource = true)
    {
        if (_fileHistoryPanel is not { } panel) return;
        var active = (TopLevel.GetTopLevel(panel) as Window)?.IsActive == true;
        _fileHistoryPanel = null; _dock?.SetAvailable(ToolId.FileHistory, false); FileHistoryHost.Content = null; panel.Dispose();
        if (focusSource && !_closed && active) { Activate(); RequestSourceFocus(); }
    }
}
