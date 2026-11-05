using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private readonly Grid _gitOutputPanel = new() { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8, Margin = new(12, 8) };
    private readonly TextBlock _gitOutputStatus = new() { Classes = { "dialog-hint" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _gitOutputText = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "code" }, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Button _gitTerminate = new() { Content = "终止外部 Git 进程…", Classes = { "dialog-action" } };
    private readonly DispatcherTimer _gitTaskTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _gitTerminating;

    private void InitializeGitTasks()
    {
        _gitOutputPanel.Children.Add(_gitOutputStatus);
        _gitOutputPanel.Children.Add(_gitOutputText); Grid.SetRow(_gitOutputText, 1);
        AutomationProperties.SetName(_gitOutputText, "Git 命令输出");
        var hide = new Button { Content = "隐藏", Classes = { "dialog-action" } };
        hide.Click += (_, _) => { _dock?.Hide(ToolId.Output); FocusEditor(); };
        _gitTerminate.Click += async (_, _) => await TerminateGitTaskAsync();
        var actions = DialogChrome.Buttons(_gitTerminate, hide); _gitOutputPanel.Children.Add(actions); Grid.SetRow(actions, 2);
        _model.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(_model.LastGitOperation) or nameof(_model.LastOperationOutput)) UpdateGitOutput(); };
        _gitTaskTimer.Tick += (_, _) =>
        {
            var state = _model.LastGitOperation?.Snapshot();
            GitTaskButton.IsVisible = state is not null && state.Root == _model.Root;
            if (state is not null)
            {
                GitTaskLabel.Text = state.Running ? $"{state.Name} · {(int)state.Elapsed.TotalSeconds}s" : $"{state.Name} · {state.Phase}";
                ToolTip.SetTip(GitTaskButton, $"{state.Name} · {state.Phase}\n在主界面查看输出；隐藏面板不停止任务");
            }
            if (_dock?.Visible(ToolId.Output) == true) UpdateGitOutput();
        };
        _gitTaskTimer.Start(); Closed += (_, _) => _gitTaskTimer.Stop();
    }

    private void GitTaskClicked(object? sender, RoutedEventArgs e) => ShowGitTask();
    private void ShowGitTask() { _dock?.Show(ToolId.Output); UpdateGitOutput(); }

    private void UpdateGitOutput()
    {
        var state = _model.LastGitOperation?.Snapshot();
        var current = state is not null && state.Root == _model.Root;
        if (!_gitTerminating) _gitOutputStatus.Text = current ? $"{state!.Name} · {state.Phase} · {state.Elapsed.TotalSeconds:F0} 秒\n{state.Root}" : _model.Root ?? "尚未打开仓库";
        var text = current ? (state!.Running || _model.LastOperationOutput.Length == 0 ? state.Output : _model.LastOperationOutput) : "";
        if (_gitOutputText.Text != text) _gitOutputText.Text = text;
        _gitOutputText.PlaceholderText = current && state!.Running ? "正在等待 Git 输出…" : "当前仓库尚无 Git 命令输出";
        _gitTerminate.IsEnabled = !_gitTerminating && current && state is { Running: true, TerminationRequested: false, ProcessId: not null };
    }

    private async Task TerminateGitTaskAsync(Window? owner = null, GitWriteOperation? expected = null)
    {
        if (_gitTerminating || _model.LastGitOperation is not { } operation || operation.Snapshot() is not { ProcessId: { } pid } state || state.Root != _model.Root) return;
        if (expected is not null && !ReferenceEquals(expected, operation)) return;
        _gitTerminating = true; _gitTerminate.IsEnabled = false;
        try
        {
            if (await ConfirmDialog.ShowAsync(owner ?? _dock?.WindowFor(ToolId.Output) ?? this, "终止外部 Git 进程", $"将终止此任务的 Git 进程及其子进程（PID {pid}）。\n\n写操作可能已部分完成，远端可能已部分更新。退出后请刷新本地状态；远端结果需重新拉取核对。此操作不是回滚，也不会删除锁文件。", "终止进程"))
            {
                if (_model.Root != state.Root || !ReferenceEquals(operation, _model.LastGitOperation)) return;
                if (!await Task.Run(() => operation.Terminate(pid))) _gitOutputStatus.Text = "确认期间该 Git 进程已结束，没有终止其他命令。";
            }
        }
        catch (Exception ex) { _model.ReportError(ex.Message); }
        finally { _gitTerminating = false; UpdateGitOutput(); }
    }
}
