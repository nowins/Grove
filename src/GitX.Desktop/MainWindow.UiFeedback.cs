using Avalonia.Automation;
using Avalonia.Controls;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private bool _refreshFeedbackPending;
    private long _refreshFeedbackVersion;

    private void AddFileFilterClear(TextBox input)
    {
        var clear = new Button { Classes = { "toolbar", "filter-clear" }, Content = IconContent.Icon("CloseIcon"), IsEnabled = false };
        AutomationProperties.SetName(clear, "清除文件筛选");
        ToolTip.SetTip(clear, "清除文件筛选");
        input.InnerRightContent = clear;
        input.TextChanged += (_, _) => clear.IsEnabled = !string.IsNullOrEmpty(input.Text);
        clear.Click += (_, _) => { input.Text = ""; input.Focus(); };
    }

    private async Task RefreshCurrentAsync()
    {
        if (_refreshFeedbackPending || _closed) return;
        if (_model.IsWriting) { await _model.RefreshAsync(); return; }
        var root = _model.Root;
        var version = ++_refreshFeedbackVersion;
        _refreshFeedbackPending = true;
        FileRefreshButton.IsEnabled = WorkspaceRefreshButton.IsEnabled = false;
        FileRefreshFeedback.Text = "正在刷新…";
        ToolTip.SetTip(WorkspaceRefreshButton, "正在重新读取仓库…");
        try { await _model.RefreshAsync(); }
        finally
        {
            _refreshFeedbackPending = false;
            if (!_closed)
            {
                FileRefreshButton.IsEnabled = WorkspaceRefreshButton.IsEnabled = true;
                ToolTip.SetTip(WorkspaceRefreshButton, "重新读取仓库 · F5");
                FileRefreshFeedback.Text = root != _model.Root ? "" : _model.HasError ? "请查看错误提示" : "已刷新";
                _ = ClearRefreshFeedbackAsync(version);
            }
        }
    }

    private async Task ClearRefreshFeedbackAsync(long version)
    {
        await Task.Delay(2200);
        if (!_closed && version == _refreshFeedbackVersion && !_refreshFeedbackPending && !_model.HasError)
            FileRefreshFeedback.Text = "";
    }
}
