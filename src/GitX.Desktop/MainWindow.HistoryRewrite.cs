using Avalonia.Interactivity;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private async Task RewordHistoryAsync(ActionContext context, CommitEntry commit, Func<bool> current)
    {
        if (commit.Sha != context.Head)
        {
            var plan = await HistoryRewrite.PrepareAsync(context, commit.Sha);
            if (!current()) return;
            await ReviewRewriteAsync(plan, plan.Commits.Select(c => new RewriteStep(c.Sha, c.Sha == commit.Sha ? RewriteAction.Reword : RewriteAction.Pick, c.Message)).ToArray(), "修改提交说明", current);
            return;
        }
        var info = await CommitLogActions.ReadAsync(context.Root, commit.Sha);
        if (!current()) return;
        var message = await InputDialog.ShowAsync(this, "修改提交说明", $"{context.Branch} · {commit.ShortSha}\n只修改当前提交说明，原文件树、父提交与作者保留；已有暂存和未暂存改动保留。", info.Message, multiline: true, required: true, inputLabel: "提交说明", acceptText: "修改说明");
        if (message is null || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => HistoryRewrite.RewordHeadAsync(context, commit.Sha, message), "修改提交说明");
    }
    private async Task UndoHistoryAsync(ActionContext context, CommitEntry commit, Func<bool> current)
    {
        if (!await ConfirmDialog.ShowAsync(this, "Undo Commit", $"将 {context.Branch} 从 {commit.ShortSha} 退回其父提交。\n提交改动和已有暂存内容留在暂存区，未暂存文件保留；执行前保存原历史恢复记录。", "撤回提交") || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => HistoryRewrite.UndoAsync(context, commit.Sha), "Undo Commit");
    }
    private async Task RewriteHistorySelectionAsync(ActionContext context, CommitEntry[] commits, RewriteAction action, string title, Func<bool> current)
    {
        var choice = await HistoryRewrite.PrepareSelectionAsync(context, commits.Select(c => c.Sha), action);
        if (current()) await ReviewRewriteAsync(choice.Plan, choice.Steps, title, current);
    }
    private async Task InteractiveHistoryRewriteAsync(ActionContext context, CommitEntry commit, Func<bool> current)
    {
        var plan = await HistoryRewrite.PrepareAsync(context, commit.Sha);
        if (current()) await ReviewRewriteAsync(plan, plan.Commits.Select(c => new RewriteStep(c.Sha, RewriteAction.Pick)).ToArray(), "交互式 Rebase", current);
    }
    private async Task ReviewRewriteAsync(HistoryRewritePlan plan, IReadOnlyList<RewriteStep> initial, string title, Func<bool> current)
    {
        if (!current()) return;
        var steps = await HistoryRewriteDialog.ShowAsync(this, title, plan, initial);
        if (steps is null || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(plan.Context.Root, () => HistoryRewrite.ExecuteAsync(plan, steps), title);
        if (_model.HasOperation && _model.NavigationEntries(1).Any(e => e.Kind == ComparisonKind.Conflict)) ShowConflictsClicked(null, new RoutedEventArgs());
    }
}
