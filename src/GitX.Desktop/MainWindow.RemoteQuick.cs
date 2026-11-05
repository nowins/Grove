using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private async Task OpenRemoteMenuAsync(Control anchor)
    {
        if (_model.Root is not { } root || _model.IsWriting || ToggleShellPopup(anchor)) return;
        var generation = _shellGeneration;
        try
        {
            var context = await RepositoryActions.ReadAsync(root);
            var remotes = await new RemoteWriter().ReadAsync(root);
            if (_closed || root != _model.Root || generation != _shellGeneration || _model.IsWriting) return;
            if (remotes.Count == 0) { _model.ReportError("当前仓库未配置远程，请先配置远程地址。"); return; }
            var target = RemoteQuickTarget.Resolve(context, remotes);
            var panel = new StackPanel { Spacing = 0, MinWidth = 260, MaxWidth = 420 };
            panel.Children.Add(new TextBlock { Text = "同步 · " + context.Branch, Classes = { "filter-summary" }, Margin = new(8, 5, 8, 7) });
            foreach (var remote in remotes)
            {
                var fetch = ShellAction("Fetch · " + remote.Name, "RemoteIcon", () =>
                { CloseShellPopup(true); _ = RunQuickRemoteAsync(context, remote, RemoteAction.Fetch, ""); }, context.InOperation ? "请先完成当前 Git 操作" : null);
                ToolTip.SetTip(fetch, "获取远程引用 · " + RemoteWriter.RedactUrl(remote.Url));
                panel.Children.Add(fetch);
            }
            panel.Children.Add(new Separator());
            if (target is not null)
            {
                var destination = target.Remote.Name + "/" + target.Branch;
                panel.Children.Add(ShellAction("Pull · " + destination + "（仅快进）", "DownIcon", () =>
                { CloseShellPopup(true); _ = RunQuickRemoteAsync(context, target.Remote, RemoteAction.Pull, target.Branch); },
                    context.Dirty ? "有未提交改动，请先提交或 Stash" : context.InOperation ? "请先完成当前 Git 操作" : null));
                var push = ShellAction("Push · " + context.Branch + " → " + destination, "UpIcon", () =>
                { CloseShellPopup(true); _ = RunQuickRemoteAsync(context, target.Remote, RemoteAction.Push, target.Branch); },
                    context.Head is null ? "没有可推送的提交" : context.InOperation ? "请先完成当前 Git 操作" : null);
                ToolTip.SetTip(push, "推送当前提交到 " + destination + "\n" + RemoteWriter.RedactUrl(target.Remote.PushUrl));
                AutomationProperties.SetName(push, "推送到 " + destination);
                panel.Children.Add(push);
            }
            else panel.Children.Add(new TextBlock { Text = "目标不明确，请在高级选项中选择远程和分支", Classes = { "filter-summary" }, Margin = new(8, 6) });
            panel.Children.Add(new Separator());
            panel.Children.Add(ShellAction("高级选项…", "SettingsIcon", () => { CloseShellPopup(); _ = OpenRemoteAsync(); }));
            var popup = new Flyout { Content = panel }; popup.FlyoutPresenterClasses.Add("shell-popup");
            ShowShellPopup(popup, anchor);
        }
        catch (Exception ex) { if (!_closed && root == _model.Root && generation == _shellGeneration) _model.ReportError(ex.Message); }
    }

    private async Task RunQuickRemoteAsync(ActionContext context, RemoteEntry remote, RemoteAction action, string branch)
    {
        if (_closed || _model.Root != context.Root || _model.IsWriting) return;
        if (action == RemoteAction.Push)
        {
            await OpenPushReviewAsync(context, remote, branch, requireChoice: context.Branch != branch);
            return;
        }
        try
        {
            await using var askPass = new GitAskPassSession(Environment.ProcessPath!, prompt => Dispatcher.UIThread.InvokeAsync(() =>
                InputDialog.ShowAsync(this, "Git 凭据", prompt + "\n凭据仅传给当前 Git 进程。", secret: !prompt.StartsWith("Username", StringComparison.OrdinalIgnoreCase))));
            var label = action + " · " + remote.Name + (action == RemoteAction.Fetch ? "" : "/" + branch);
            await _model.ExecuteActionAsync(context.Root, () => new RemoteWriter().ExecuteAsync(context, remote, action, branch,
                authentication: askPass.Authentication), label);
        }
        catch (Exception ex) { if (!_closed && _model.Root == context.Root) _model.ReportError(ex.Message); }
    }

    private async Task PushAfterCommitAsync()
    {
        if (_model.Root is not { } root || _model.IsWriting) return;
        try
        {
            var context = await RepositoryActions.ReadAsync(root);
            var target = RemoteQuickTarget.Resolve(context, await new RemoteWriter().ReadAsync(root));
            if (_closed || _model.Root != root) return;
            if (target is not null) await RunQuickRemoteAsync(context, target.Remote, RemoteAction.Push, target.Branch);
            else await OpenRemoteMenuAsync(RemoteButton);
        }
        catch (Exception ex) { if (!_closed && _model.Root == root) _model.ReportError(ex.Message); }
    }
    private async Task OpenPushReviewAsync(ActionContext context, RemoteEntry? remote, string? branch,
        bool requireChoice = false, bool force = false, bool ephemeral = false)
    {
        var generation = _model.RepositoryGeneration;
        var remotes = await new RemoteWriter().ReadAsync(context.Root);
        if (_closed || _model.Root != context.Root || _model.IsWriting || generation != _model.RepositoryGeneration) return;
        var dialog = new PushReviewWindow(context.Root, context, remotes, remote, branch, requireChoice, force, ephemeral,
            () => !_closed && _model.Root == context.Root && generation == _model.RepositoryGeneration,
            action => _model.ExecuteActionAsync(context.Root, action, "Push · 审阅目标"),
            () => _model.LastGitOperation, (owner, operation) => TerminateGitTaskAsync(owner, operation));
        await dialog.ShowDialog(this);
    }

}
