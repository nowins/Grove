using Avalonia.Interactivity;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private async Task RebaseOntoHistoryAsync(ActionContext context, CommitEntry commit, Func<bool> current)
    {
        var source = context.Detached ? "分离 HEAD" : context.Branch;
        if (!await ConfirmDialog.ShowAsync(this, "变基到所选提交", $"将 {source} 的提交重放到：\n{commit.Sha}\n{commit.Subject}\n\n这会改写当前历史，执行前保留原历史恢复记录。冲突时暂停，可继续、跳过或中止。", "开始变基") || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => new CommitReferenceWriter().RebaseOntoAsync(context, commit.Sha), "变基到所选提交");
        if (_model.HasOperation && _model.NavigationEntries(1).Any(e => e.Kind == ComparisonKind.Conflict)) ShowConflictsClicked(null, new RoutedEventArgs());
    }
}
