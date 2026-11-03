using GitX.Core;
using GitX.Desktop.Controls;
using GitX.Desktop.ViewModels;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly WorktreePanel _worktreesPanel = new();
    private long _worktreeChoiceGeneration;
    private string? _worktreeFailureMessage;

    private void InitializeWorktreeTool()
    {
        _worktreesPanel.RefreshRequested += () => _ = RefreshWorktreePanelAsync();
        _worktreesPanel.OpenRequested += (snapshot, selected) => _ = OpenWorktreeAsync(snapshot, selected);
        _worktreesPanel.RemoveRequested += (snapshot, selected) => _ = RemoveWorktreeAsync(snapshot, selected);
        _model.PropertyChanged += (_, _) => _worktreesPanel.SetCanOpen(!_model.Busy && !_model.IsWriting && _model.Root == _worktreesPanel.Snapshot?.Root);
        _model.RepositoryOpened += root =>
        {
            if (_worktreesPanel.Snapshot?.Root != root) { ++_worktreeChoiceGeneration; _worktreesPanel.Clear(root); }
            if (_dock?.Visible(ToolId.Worktrees) == true) _ = RefreshWorktreePanelAsync();
        };
    }

    private Task OpenWorktreesAsync()
    {
        CloseShellPopup(); _dock?.Show(ToolId.Worktrees, focus: false); _worktreesPanel.FocusList();
        return RefreshWorktreePanelAsync();
    }

    private async Task RefreshWorktreePanelAsync()
    {
        var generation = ++_worktreeChoiceGeneration;
        if (_model.Root is not { } root) { _worktreesPanel.Clear(null); _worktreesPanel.SetBusy(false); return; }
        if (_worktreesPanel.Snapshot?.Root != root) _worktreesPanel.Clear(root);
        _worktreesPanel.SetBusy(true);
        try
        {
            var snapshot = await WorktreeCatalog.ReadAsync(root);
            if (_closed || root != _model.Root || generation != _worktreeChoiceGeneration) return;
            _worktreesPanel.Present(snapshot);
        }
        catch (Exception ex)
        { if (!_closed && root == _model.Root && generation == _worktreeChoiceGeneration) _worktreesPanel.ShowError("无法读取工作树：" + ex.Message + " 可点击刷新重试。"); }
        finally
        { if (!_closed && generation == _worktreeChoiceGeneration) { _worktreesPanel.SetBusy(false); _worktreesPanel.SetCanOpen(!_model.Busy && !_model.IsWriting && _model.Root == _worktreesPanel.Snapshot?.Root); } }
    }

    private async Task OpenWorktreeAsync(WorktreeSnapshot snapshot, WorktreeEntry selected)
    {
        if (snapshot.Root != _model.Root || selected.Current || selected.UnavailableReason is not null || !CanChangeRepository()) return;
        var generation = ++_worktreeChoiceGeneration; _worktreesPanel.SetBusy(true);
        try
        {
            var destination = await WorktreeCatalog.ResolveOpenAsync(snapshot, selected);
            if (_closed || snapshot.Root != _model.Root || generation != _worktreeChoiceGeneration || !CanChangeRepository()) return;
            await OpenRepositoryAsync(destination);
        }
        catch (Exception ex)
        {
            if (!_closed && snapshot.Root == _model.Root && generation == _worktreeChoiceGeneration)
            {
                _worktreeFailureMessage = $"无法打开工作树：{selected.Root}\n{ex.Message}\n当前仓库未切换。请刷新工作树列表后重试。";
                _worktreesPanel.ShowError(_worktreeFailureMessage);
            }
        }
        finally
        { if (!_closed && generation == _worktreeChoiceGeneration) _worktreesPanel.SetBusy(false); }
    }

    private async Task RemoveWorktreeAsync(WorktreeSnapshot snapshot, WorktreeEntry selected)
    {
        if (snapshot.Root != _model.Root || !CanChangeRepository()) return;
        var generation = ++_worktreeChoiceGeneration; _worktreesPanel.SetBusy(true);
        try
        {
            var plan = await WorktreeRemoval.PrepareAsync(snapshot, selected);
            if (_closed || snapshot.Root != _model.Root || generation != _worktreeChoiceGeneration || !CanChangeRepository()) return;
            var confirmed = await ConfirmDialog.ShowAsync(this, "删除关联工作树",
                $"仓库：{snapshot.Root}\n\n删除目录：{plan.Target.Root}\n分支：{plan.Target.BranchLabel}\n提交：{plan.Target.Head ?? "暂无提交"}\n\n将由 Git 删除此关联工作树目录及登记，保留分支与提交。目录删除无法在 Grove 中撤销。\n已检查未提交、未跟踪、被忽略文件和锁定状态；执行前还会再次检查。请先关闭使用该目录的其他程序。", "删除工作树");
            if (!confirmed || _closed || snapshot.Root != _model.Root || generation != _worktreeChoiceGeneration || !CanChangeRepository()) return;
            await _model.ExecuteActionAsync(snapshot.Root, () => WorktreeRemoval.RemoveAsync(plan), "删除关联工作树");
            if (!_closed && snapshot.Root == _model.Root) await RefreshWorktreePanelAsync();
        }
        catch (Exception ex)
        {
            if (!_closed && snapshot.Root == _model.Root && generation == _worktreeChoiceGeneration)
                _worktreesPanel.ShowError("未删除工作树：" + ex.Message);
        }
        finally
        {
            if (!_closed && generation == _worktreeChoiceGeneration) _worktreesPanel.SetBusy(false);
        }
    }
}
