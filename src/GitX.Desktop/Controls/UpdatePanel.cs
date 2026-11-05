using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core.Updates;
using GitX.Desktop.Services;

namespace GitX.Desktop.Controls;

internal sealed class UpdatePanel : Border
{
    private readonly TextBlock _status = new() { Text = "尚未检查更新。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _progressText = new() { Classes = { "muted" } };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 4, IsVisible = false };
    private readonly Button _check = new() { Content = "检查更新", Classes = { "dialog-action" } };
    private readonly Button _download = new() { Content = "后台下载", Classes = { "dialog-action" }, IsEnabled = false };
    private readonly Button _install = new() { Content = "安装…", Classes = { "dialog-action", "primary" }, IsEnabled = false };
    private readonly Button _cancel = new() { Content = "取消", Classes = { "dialog-action" }, IsEnabled = false };
    private readonly CheckBox _automatic = new() { Content = "自动检查更新（启动时及每 6 小时）" };
    private readonly CheckBox _background = new() { Content = "发现新版本后在后台下载" };
    private readonly CheckBox _preview = new() { Content = "包含预览版本" };
    private readonly Button _releases = new() { Classes = { "toolbar" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly bool _supportsInAppUpdates;
    private bool _setting;
    public event Action? CheckRequested, DownloadRequested, InstallRequested, CancelRequested;
    public event Action<UpdatePreferences>? PreferencesChanged;
    public UpdatePanel(string current, string rid, bool supportsInAppUpdates = true)
    {
        _supportsInAppUpdates = supportsInAppUpdates;
        Padding = new(16, 12);
        var body = new StackPanel { Spacing = 10, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        var header = new WrapPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = "Grove " + current, FontWeight = FontWeight.Medium, Margin = new(0, 0, 12, 0) });
        header.Children.Add(new TextBlock { Text = rid + " · nowins/Grove", Classes = { "muted" } }); body.Children.Add(header);
        body.Children.Add(_status); body.Children.Add(_detail); body.Children.Add(_progress); body.Children.Add(_progressText);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _check, _download, _install, _cancel }) { button.Margin = new(0, 0, 8, 4); buttons.Children.Add(button); }
        body.Children.Add(buttons);
        body.Children.Add(_automatic); body.Children.Add(_background); body.Children.Add(_preview);
        body.Children.Add(new TextBlock { Text = supportsInAppUpdates ? "通过公开 GitHub Releases 检查更新，无需登录或安装 GitHub CLI。Grove 不发送凭据。下载可在后台进行；安装始终需要确认。" : "此平台不运行启动时或定时检查，不后台下载或自动安装。点击下方按钮在浏览器发现正式版本和预览版本。", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = supportsInAppUpdates ? "当前包未提供 Windows 开发者签名或 Apple 公证保证。SHA-256 只验证内容完整性，不代替发布者认证或系统安全检查。" : "Linux 开发包尚未提供发行签名。SHA-256 只验证内容完整性，不代替来源核对；请保留当前应用和用户数据直至新版本验证可用。", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        _releases.Content = supportsInAppUpdates ? "在浏览器查看正式 Releases" : "查看 Releases 并手动更新…";
        _releases.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GitHubUpdateSource.ReleasesUrl) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { _status.Text = "无法打开系统浏览器。"; _detail.Text = GitHubUpdateSource.ReleasesUrl + "\n" + error.Message; }
        };
        body.Children.Add(_releases);
        Child = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _check.Click += (_, _) => CheckRequested?.Invoke(); _download.Click += (_, _) => DownloadRequested?.Invoke(); _install.Click += (_, _) => InstallRequested?.Invoke(); _cancel.Click += (_, _) => CancelRequested?.Invoke();
        foreach (var box in new[] { _automatic, _background, _preview }) box.IsCheckedChanged += (_, _) => { if (!_setting) PreferencesChanged?.Invoke(new(_automatic.IsChecked == true, _background.IsChecked == true, _preview.IsChecked == true)); };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, "安装包下载进度");
    }
    public void Preferences(UpdatePreferences preferences)
    { _setting = true; _automatic.IsChecked = preferences.AutomaticChecks; _background.IsChecked = preferences.BackgroundDownload; _preview.IsChecked = preferences.IncludePreviews; _setting = false; }
    public void Present(string status, string detail, bool busy, bool canDownload, bool canInstall, bool error = false)
    {
        busy &= _supportsInAppUpdates;
        _status.Text = status; _detail.Text = detail;
        _status.Foreground = (IBrush)Application.Current!.FindResource(error ? "ErrorTextBrush" : "TextBrush")!;
        _check.IsEnabled = _supportsInAppUpdates && !busy; _download.IsEnabled = _supportsInAppUpdates && !busy && canDownload; _install.IsEnabled = _supportsInAppUpdates && !busy && canInstall; _cancel.IsEnabled = busy;
        _automatic.IsEnabled = _background.IsEnabled = _preview.IsEnabled = _supportsInAppUpdates && !busy;
        _progress.IsVisible = busy; _progress.IsIndeterminate = busy; _progressText.Text = "";
    }
    public void Progress(long bytes, long total)
    { _progress.IsVisible = true; _progress.IsIndeterminate = false; _progress.Value = total > 0 ? bytes * 100.0 / total : 0; _progressText.Text = $"{bytes / 1048576.0:F1} / {total / 1048576.0:F1} MiB"; }
    public void FocusCheck() => (_supportsInAppUpdates ? _check : _releases).Focus();
}
