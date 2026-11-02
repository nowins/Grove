using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private long _worktreeChoiceGeneration;
    private string? _worktreeFailureMessage;

    private async Task OpenWorktreesAsync()
    {
        if (_model.Root is not { } root || !CanChangeRepository()) return;
        CloseShellPopup();
        var generation = ++_worktreeChoiceGeneration;
        string? target = null;
        try
        {
            var snapshot = await WorktreeCatalog.ReadAsync(root);
            if (_closed || root != _model.Root || generation != _worktreeChoiceGeneration || !CanChangeRepository()) return;
            var selected = await WorktreeDialog.ShowAsync(this, snapshot);
            if (selected is null || _closed || root != _model.Root || generation != _worktreeChoiceGeneration) return;
            target = selected.Root;
            var destination = await WorktreeCatalog.ResolveOpenAsync(snapshot, selected);
            if (_closed || root != _model.Root || generation != _worktreeChoiceGeneration || !CanChangeRepository()) return;
            await OpenRepositoryAsync(destination);
        }
        catch (Exception ex)
        {
            if (!_closed && root == _model.Root && generation == _worktreeChoiceGeneration)
            {
                _worktreeFailureMessage = $"无法打开工作树：{target ?? root}\n{ex.Message}\n当前仓库未切换。请重新选择工作树以刷新目录状态。";
                _model.ReportError(_worktreeFailureMessage);
            }
        }
    }
}
