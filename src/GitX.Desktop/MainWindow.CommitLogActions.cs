using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private async Task CopyHistoryMessageAsync(string root, CommitEntry commit, Func<bool> current)
    {
        var full = await CommitLogActions.ReadAsync(root, commit.Sha);
        if (!current()) return;
        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。");
        await clipboard.SetTextAsync(full.Message);
    }

    private async Task<string?> ChooseHistoryParentAsync(string root, CommitEntry commit, Func<bool> current)
    {
        var full = await CommitLogActions.ReadAsync(root, commit.Sha);
        if (!current() || full.Parents!.Count == 0) return null;
        if (full.Parents.Count == 1) return full.Parents[0];
        var choices = await Task.WhenAll(full.Parents.Select(sha => CommitLogActions.ReadAsync(root, sha)));
        if (!current()) return null;
        return await CommitChoiceDialog.ShowAsync(this, "选择父提交", $"{commit.ShortSha} · {commit.Subject}\n请选择父提交；顺序与 Git 的父提交顺序一致。", choices);
    }

    private async Task ExportHistoryPatchAsync(string root, CommitEntry commit, Func<bool> current)
    {
        var parent = await ChooseHistoryParentAsync(root, commit, current);
        if (!current() || commit.Parents?.Count > 1 && parent is null) return;
        var patch = await CommitLogActions.PatchAsync(root, commit.Sha, parent);
        if (!current()) return;
        var target = await InteractionWindow.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = $"创建提交补丁 · {parent?[..8] ?? "初始提交"} → {commit.ShortSha}",
            SuggestedFileName = commit.ShortSha + ".patch", FileTypeChoices = [new("Git 补丁") { Patterns = ["*.patch"] }]
        });
        if (target is null || !current()) return;
        await using var stream = await target.OpenWriteAsync(); stream.SetLength(0);
        await stream.WriteAsync(patch);
    }

    private async Task CreateHistoryBranchAsync(ActionContext context, CommitEntry commit, Func<bool> current)
    {
        var choice = await BranchCreateDialog.ShowAsync(this, context, null, commit);
        if (choice is null || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => new CommitReferenceWriter().CreateBranchAsync(context, commit.Sha, choice.Name, choice.Checkout), "从提交新建分支");
    }

    private async Task CreateHistoryTagAsync(ActionContext context, CommitEntry commit, Func<bool> current)
    {
        var name = await InputDialog.ShowAsync(this, "从提交新建标签", $"{commit.ShortSha} · {commit.Subject}\n\n创建本地轻量标签，不推送到远端，也不覆盖已有标签。",
            required: true, inputLabel: "标签名称", acceptText: "创建");
        if (name is null || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => new CommitReferenceWriter().CreateTagAsync(context, commit.Sha, name), "从提交新建标签");
    }

    private async Task CheckoutHistoryRevisionAsync(ActionContext context, CommitEntry commit, Func<bool> current)
    {
        if (!await ConfirmDialog.ShowAsync(this, "检出此修订", $"{context.Branch} → {commit.ShortSha}\n{commit.Subject}\n\n进入分离 HEAD，工作区切换到此提交。若要继续提交，可先从此处新建分支。", "检出修订") || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => new CommitReferenceWriter().CheckoutAsync(context, commit.Sha), "检出修订");
    }

    private async Task NavigateHistoryParentAsync(string root, CommitEntry commit, Func<bool> current)
    {
        var sha = await ChooseHistoryParentAsync(root, commit, current);
        if (sha is not null && current()) await RevealHistoryCommitAsync(sha);
    }

    private async Task NavigateHistoryChildAsync(string root, CommitEntry commit, Func<bool> current)
    {
        var choices = await CommitLogActions.ChildrenAsync(root, commit.Sha);
        if (!current()) return;
        if (choices.Count == 0) { _model.ReportStatus("此提交在可达历史中没有子提交。"); return; }
        var sha = choices.Count == 1 ? choices[0].Sha : await CommitChoiceDialog.ShowAsync(this, "选择子提交", $"{commit.ShortSha} · {commit.Subject}\n请选择要定位的子提交。", choices);
        if (sha is not null && current()) await RevealHistoryCommitAsync(sha);
    }

    private async Task RevealHistoryCommitAsync(string sha)
    {
        await _model.NavigateHistoryCommitAsync(sha);
        if (_closed || _model.SelectedCommit?.Sha != sha || !_bottomOpen) return;
        CommitList.ScrollIntoView(_model.SelectedCommit);
        (CommitList.ContainerFromIndex(CommitList.SelectedIndex) ?? CommitList).Focus(NavigationMethod.Tab);
    }
}
