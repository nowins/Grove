using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private Window? _gitTaskWindow;
    private readonly DispatcherTimer _gitTaskTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private void InitializeGitTasks()
    {
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_model.LastGitOperation) && _gitTaskWindow is not null)
            { _gitTaskWindow.Close(); ShowGitTask(); }
        };
        _gitTaskTimer.Tick += (_, _) =>
        {
            var state = _model.LastGitOperation?.Snapshot();
            GitTaskButton.IsVisible = state is not null;
            if (state is not null)
            {
                GitTaskLabel.Text = state.Running ? $"{state.Name} · {(int)state.Elapsed.TotalSeconds}s" : $"{state.Name} · {state.Phase}";
                ToolTip.SetTip(GitTaskButton, $"{state.Name} · {state.Phase}\n点击查看任务详情与输出");
            }
        };
        _gitTaskTimer.Start();
        Closed += (_, _) => { _gitTaskTimer.Stop(); _gitTaskWindow?.Close(); };
    }

    private void GitTaskClicked(object? sender, RoutedEventArgs e) => ShowGitTask();

    private void ShowGitTask()
    {
        if (_gitTaskWindow is not null) { _gitTaskWindow.Activate(); return; }
        if (_model.LastGitOperation is not { } operation) return;
        var dialog = new Window { Title = "Git 任务", Width = 720, Height = 440, MinWidth = 500, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _gitTaskWindow = dialog;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "code" },
            VerticalAlignment = VerticalAlignment.Stretch };
        var terminate = new Button { Content = "终止本次 Git 进程…" };
        var browse = new Button { Content = "返回浏览", IsCancel = true };
        browse.Click += (_, _) => dialog.Close();
        var confirming = false;
        terminate.Click += async (_, _) =>
        {
            if (confirming || operation.Snapshot().ProcessId is not { } pid) return;
            confirming = true; terminate.IsEnabled = false;
            try
            {
                if (await ConfirmDialog.ShowAsync(dialog, "终止本次 Git 进程", $"将终止此任务的 Git 进程及其子进程（PID {pid}）。\n\n工作区、索引或远程可能已部分更新。结束后 GitX 会重新读取本地状态；远程结果需获取核对。此操作不会回滚，也不会删除锁文件。", "终止进程"))
                {
                    if (!await Task.Run(() => operation.Terminate(pid))) status.Text = "确认期间该 Git 命令已结束，没有终止后续命令。";
                }
            }
            catch (Exception ex) { _model.ReportError(ex.Message); }
            finally { confirming = false; }
        };
        void Update()
        {
            // A reopened window follows the most recent task, but an already
            // open confirmation remains bound to its original operation/PID.
            var state = operation.Snapshot();
            if (!confirming) status.Text = $"{state.Name} · {state.Phase} · {state.Elapsed.TotalSeconds:F0} 秒\n{state.Root}";
            if (output.Text != state.Output) output.Text = state.Output;
            output.PlaceholderText = state.Running ? "正在等待 Git 输出…" : "此任务没有输出。";
            terminate.IsEnabled = !confirming && state.Running && !state.TerminationRequested && state.ProcessId is not null;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Update();
        dialog.Closed += (_, _) => { timer.Stop(); if (_gitTaskWindow == dialog) _gitTaskWindow = null; };
        var panel = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        panel.Children.Add(status); Grid.SetRow(output, 1); panel.Children.Add(output);
        var footer = DialogChrome.Footer(DialogChrome.Buttons(terminate, browse));
        Grid.SetRow(footer, 2); panel.Children.Add(footer); dialog.Content = panel;
        DialogChrome.Apply(dialog);
        Update(); timer.Start(); dialog.Show(this);
    }
}
