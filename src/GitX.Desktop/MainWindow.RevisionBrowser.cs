using Avalonia.Controls;
using Avalonia.Interactivity;
using GitX.Core;
using GitX.Desktop.Controls;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly Grid _projectHost = new();
    private RevisionBrowserPanel? _revisionBrowser;
    private async Task OpenRevisionBrowserAsync(string root, CommitEntry commit, Func<bool> current)
    {
        if (!current()) return;
        var owner = _model.RepositoryGeneration;
        CloseRevisionBrowser(false);
        var panel = new RevisionBrowserPanel(); _revisionBrowser = panel;
        _projectPanel.IsVisible = false; _projectHost.Children.Add(panel);
        bool Owned() => !_closed && _revisionBrowser == panel && root == _model.Root && owner == _model.RepositoryGeneration;
        panel.CloseRequested += () => CloseRevisionBrowser();
        panel.OpenRequested += async (path, sha) =>
        {
            if (!Owned()) return;
            var epoch = _inputEpoch;
            try
            {
                await _model.OpenRevisionSourceAsync(path, sha);
                if (Owned() && epoch == _inputEpoch) RequestSourceFocus();
            }
            catch (Exception error) { if (Owned()) _model.ReportError(error.Message); }
        };
        _dock?.SetAvailable(ToolId.Project, true, "修订 · " + commit.ShortSha);
        _dock?.Show(ToolId.Project, focus: false);
        await panel.OpenAsync(root, commit.Sha);
        if (Owned()) panel.FocusFiles();
    }
    private void CloseRevisionBrowser(bool focus = true)
    {
        if (_revisionBrowser is not { } panel) return;
        _revisionBrowser = null; panel.Dispose(); _projectHost.Children.Remove(panel); _projectPanel.IsVisible = true;
        _dock?.SetAvailable(ToolId.Project, true, "项目");
        if (focus && !_closed) NativeKeyboardFocus.TryFocusControl(this, _projectList);
    }
    private async void SkipOperationClicked(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanSkipOperation || _model.CaptureOperation() is not { } context) return;
        var repository = _model.RepositoryGeneration;
        if (await ConfirmDialog.ShowAsync(this, "跳过当前提交", "丢弃当前提交的冲突处理或空改动，继续处理本批剩余提交；已有文件修改会先保留恢复副本。", "跳过提交") && repository == _model.RepositoryGeneration)
            await _model.RunOperationAsync(context, false, skip: true);
    }
    private async Task RunHistoryBatchAsync(ActionContext context, CommitEntry[] commits, HistoryAction action, string label, Func<bool> current)
    {
        var plan = await HistoryBatch.PrepareAsync(context, action, commits.Select(c => c.Sha));
        if (!current()) return;
        if (plan.NeedsMainline)
        {
            var merges = plan.Commits.Where(c => c.Parents?.Count > 1);
            var value = await InputDialog.ShowAsync(this, "选择主线父提交", string.Join('\n', merges.Select(c => c.ShortSha + " · " + c.Subject + "\n" + string.Join('\n', c.Parents!.Select((p, i) => $"  {i + 1}: {p}")))) +
                "\n本批合并提交共用主线父序号；混有普通提交时请选择 1，其他主线请分开操作。", "1");
            if (value is null || !current()) return;
            if (!int.TryParse(value, out var mainline) || mainline < 1) throw new IOException("主线父提交序号无效。");
            plan = await HistoryBatch.PrepareAsync(context, action, commits.Select(c => c.Sha), mainline);
        }
        if (!current() || !await HistoryBatchDialog.ShowAsync(this, label, plan) || !current() || _model.IsWriting) return;
        await _model.ExecuteActionAsync(context.Root, () => HistoryBatch.ExecuteAsync(plan), action == HistoryAction.CherryPick ? "批量 Cherry-pick" : "批量 Revert");
        if (_model.HasOperation && _model.NavigationEntries(1).Any(e => e.Kind == ComparisonKind.Conflict)) ShowConflictsClicked(null, new RoutedEventArgs());
    }
}
