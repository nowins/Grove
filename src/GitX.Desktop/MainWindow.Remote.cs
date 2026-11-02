using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private async Task OpenRemoteAsync()
    {
        if (_model.Root is not { } root || _model.IsWriting) return;
        try
        {
            var context = await RepositoryActions.ReadAsync(root); var writer = new RemoteWriter(); var remotes = await writer.ReadAsync(root);
            if (_closed || _model.Root != root) return;
            if (remotes.Count == 0) { _model.ReportError("当前仓库未配置远程。请先用 git remote add 配置远程，再打开此窗口。"); return; }
            var dialog = new Window { Title = "远程操作 · " + context.Branch, Width = 620, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var remote = new ComboBox { ItemsSource = remotes, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var branch = new TextBox { Text = context.Branches.FirstOrDefault(b => b.Current)?.Upstream?.Split('/', 2).LastOrDefault() ?? context.Branch };
            var action = new ComboBox { ItemsSource = new[] { "Fetch · 获取远程引用", "Pull · 仅快进", "Push · 推送当前提交", "Push · Force with lease…" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var ephemeral = new CheckBox { Content = "仅本次输入凭据（绕过已有凭据助手，不保存）" };
            var description = new TextBlock { Text = "现有 Git 凭据助手优先，缺少凭据时打开原生输入框。\nPull 仅快进；分叉时先 Fetch，再使用分支合并或变基。", TextWrapping = TextWrapping.Wrap, Classes = { "dialog-hint" } };
            var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "code" }, Height = 140, PlaceholderText = "尚未执行远程操作" };
            var run = new Button { Content = "执行…", Classes = { "primary" } }; var close = new Button { Content = "关闭", IsCancel = true }; var busy = false;
            CancellationTokenSource? reading = null;
            var cancelRead = new Button { Content = "取消读取", IsVisible = false };
            cancelRead.Click += (_, _) => reading?.Cancel();
            dialog.Closing += (_, e) => { if (busy) e.Cancel = true; }; close.Click += (_, _) => dialog.Close();
            run.Click += async (_, _) =>
            {
                if (busy || remote.SelectedItem is not RemoteEntry selectedRemote) return;
                var selectedAction = (RemoteAction)action.SelectedIndex; var target = branch.Text ?? "";
                busy = true; run.IsEnabled = close.IsEnabled = remote.IsEnabled = branch.IsEnabled = action.IsEnabled = ephemeral.IsEnabled = false;
                output.Text = "正在准备 Git 操作…";
                try
                {
                    await using var askPass = new GitAskPassSession(Environment.ProcessPath!, prompt => Dispatcher.UIThread.InvokeAsync(() =>
                        InputDialog.ShowAsync(this, "Git 凭据", prompt + "\n凭据仅传给当前 Git 进程；取消会结束凭据请求。", secret: !prompt.StartsWith("Username", StringComparison.OrdinalIgnoreCase))), ephemeral.IsChecked == true);
                    string? expected = null;
                    if (selectedAction == RemoteAction.ForceWithLease)
                    {
                        output.Text = "正在读取远程分支 SHA…";
                        using var cancellation = new CancellationTokenSource(); reading = cancellation; cancelRead.IsVisible = true;
                        try { expected = await writer.RemoteHeadAsync(context, selectedRemote, target, askPass.Authentication, cancellation.Token); }
                        finally { reading = null; cancelRead.IsVisible = false; }
                    }
                    if (selectedAction is RemoteAction.Push or RemoteAction.ForceWithLease)
                    {
                        var description = $"{context.Branch} · {context.Head?[..8]}\n→ {RemoteWriter.RedactUrl(selectedRemote.PushUrl)}\nrefs/heads/{target}";
                        if (selectedAction == RemoteAction.ForceWithLease) description += $"\n\n将改写远程历史，仅当远程仍为 { (expected == "" ? "不存在" : expected) } 时执行。";
                        if (!await ConfirmDialog.ShowAsync(dialog, "确认推送", description, selectedAction == RemoteAction.ForceWithLease ? "Force with lease" : "推送")) { output.Text = "已取消，未推送。"; return; }
                    }
                    busy = false; dialog.Close(); busy = true;
                    var write = _model.ExecuteActionAsync(root, () => writer.ExecuteAsync(context, selectedRemote, selectedAction, target, expected, askPass.Authentication), selectedAction + " · " + selectedRemote.Name);
                    await write;
                }
                catch (OperationCanceledException) { output.Text = "已取消远程读取，没有执行推送。"; }
                catch (Exception ex) { output.Text = ex.Message; }
                finally { busy = false; run.IsEnabled = close.IsEnabled = remote.IsEnabled = branch.IsEnabled = action.IsEnabled = ephemeral.IsEnabled = true; }
            };
            static StackPanel Field(string label, Control control) => new() { Spacing = 6,
                Children = { new TextBlock { Text = label, Classes = { "dialog-label" } }, control } };
            dialog.Content = DialogChrome.Layout(new StackPanel { Spacing = 12, Children = {
                Field("远程", remote), Field("目标分支（Fetch 使用远程现有获取规则）", branch), Field("操作", action),
                ephemeral, description, Field("操作输出", output) } }, DialogChrome.Buttons(cancelRead, close, run));
            DialogChrome.Apply(dialog);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex) { _model.ReportError(ex.Message); }
    }
}
