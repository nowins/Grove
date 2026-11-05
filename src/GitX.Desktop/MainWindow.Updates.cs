using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using GitX.Core.Updates;
using GitX.Desktop.Controls;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private UpdatePanel _updatesPanel = null!;
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly UpdatePreferencesStore _updateStore = new(Path.Combine(LocalDataDirectory, "update-settings.json"));
    private UpdatePreferences _updatePreferences = new();
    private UpdateOffer? _updateOffer;
    private DownloadedUpdate? _downloadedUpdate;
    private CancellationTokenSource? _updateOperation;
    private Task _updateFinished = Task.CompletedTask;
    private UpdateVersion _updateCurrent = null!;
    private string _updateRid = "", _updateMessage = "尚未检查更新。", _updateDetail = "";
    private bool _supportsInAppUpdates;
    private bool _updateBusy, _updateError, _updatesInitialized, _updateDownloading;
    private long _updateSequence;
    private long _updatePreferenceSequence;
    private string UpdateCache => Path.Combine(LocalDataDirectory, "updates");

    private void InitializeUpdateTool()
    {
        _updateCurrent = UpdateVersion.Parse(typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
            ?? throw new InvalidOperationException("应用版本无效，无法安全检查更新。");
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        _updateRid = OperatingSystem.IsWindows() ? "win-" + architecture : OperatingSystem.IsMacOS() ? "osx-" + architecture : OperatingSystem.IsLinux() ? "linux-" + architecture : "unsupported";
        _supportsInAppUpdates = UpdateOffer.SupportsRid(_updateRid);
        _updatesPanel = new(_updateCurrent.ToString(), _updateRid, _supportsInAppUpdates);
        if (!_supportsInAppUpdates)
        {
            _updateMessage = "当前平台使用手动更新。";
            _updateDetail = "在浏览器查看正式 Releases（含预览版），选择与本机架构匹配的包并核对 SHA-256。若尚无对应 Linux 包，请继续使用当前开发版本。退出 Grove 后解压新包；不会自动覆盖应用或删除 GitX 用户数据。";
        }
        _updatePreferences = new(IncludePreviews: _updateCurrent.Pre.Length > 0); _updatesPanel.Preferences(_updatePreferences);
        _updatesPanel.CheckRequested += () => { if (_updatesInitialized) _ = CheckUpdatesAsync(); };
        _updatesPanel.DownloadRequested += () => { if (_updatesInitialized) _ = DownloadUpdateAsync(); };
        _updatesPanel.InstallRequested += () => { if (_updatesInitialized) _ = InstallUpdateAsync(); };
        _updatesPanel.CancelRequested += () => _updateOperation?.Cancel();
        _updatesPanel.PreferencesChanged += preferences => { if (_updatesInitialized) _ = SaveUpdatePreferencesAsync(preferences); };
        _updateTimer.Tick += (_, _) => { if (_supportsInAppUpdates && _updatePreferences.AutomaticChecks && !_updateBusy) _ = CheckUpdatesAsync(); };
        Opened += async (_, _) =>
        {
            // Manual-only platforms leave preferences from other platforms intact.
            if (!_supportsInAppUpdates)
            { _updatesInitialized = true; RenderUpdates(); return; }
            try { _updatePreferences = await _updateStore.ReadAsync(_updateCurrent.Pre.Length > 0, _updateLifetime.Token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { _updatePreferences = new(IncludePreviews: _updateCurrent.Pre.Length > 0); _updateMessage = ex.Message; _updateError = true; }
            catch (OperationCanceledException) { return; }
            if (_closed) return;
            _updatesInitialized = true; _updatesPanel.Preferences(_updatePreferences); RenderUpdates(); _updateTimer.Start();
            if (_updatePreferences.AutomaticChecks) _ = CheckUpdatesAsync();
        };
        Closed += (_, _) => { _updateTimer.Stop(); _updateLifetime.Cancel(); _updateOperation?.Cancel(); _updateLifetime.Dispose(); };
        RenderUpdates();
    }
    private void ShowUpdates()
    { CloseShellPopup(); _dock?.Show(ToolId.Updates, focus: false); _updatesPanel.FocusCheck(); }
    private void RenderUpdates()
    {
        if (_closed) return;
        _updatesPanel.Present(_updateMessage, _updateDetail, _updateBusy || !_updatesInitialized, _updateOffer is not null, _downloadedUpdate is not null, _updateError);
        _dock?.SetAvailable(ToolId.Updates, true, _updateDownloading ? "更新 · 下载中" : _downloadedUpdate is not null ? "更新 · 已下载" : _updateOffer?.IsNewer == true ? "更新 · 可用" : "更新");
    }
    private void UpdateStatus(string message, string detail = "", bool error = false)
    { _updateMessage = message; _updateDetail = detail; _updateError = error; RenderUpdates(); }
    private async Task SaveUpdatePreferencesAsync(UpdatePreferences preferences)
    {
        if (!_supportsInAppUpdates || _updateBusy || _closed) return;
        var sequence = ++_updatePreferenceSequence;
        try
        {
            await _updateStore.WriteAsync(preferences, _updateLifetime.Token);
            if (_closed || sequence != _updatePreferenceSequence) return;
            var channelChanged = preferences.IncludePreviews != _updatePreferences.IncludePreviews;
            _updatePreferences = preferences;
            if (channelChanged) { _updateOffer = null; _downloadedUpdate = null; UpdateStatus("更新渠道已更改，请重新检查。"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!_closed && sequence == _updatePreferenceSequence) { _updatesPanel.Preferences(_updatePreferences); UpdateStatus("未能保存更新设置。", ex.Message, true); } }
        catch (OperationCanceledException) { }
    }
    private async Task RunUpdateAsync(Func<CancellationToken, Task> action)
    {
        if (!_supportsInAppUpdates || _updateBusy || _closed || !_updatesInitialized) return;
        _updateBusy = true; var operation = _updateOperation = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _updateFinished = finished.Task;
        ++_updateSequence;
        try { RenderUpdates(); await action(operation.Token); }
        catch (IOException) when (operation.IsCancellationRequested) { if (!_closed) UpdateStatus("操作已取消。", "未完成的本次下载已清理，可重试。"); }
        catch (OperationCanceledException) { if (!_closed) UpdateStatus("操作已取消。", "已校验的安装包可保留；未完成的本次下载已清理，可重试。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException)
        { if (!_closed) UpdateStatus("更新未完成。", ex.Message, true); }
        finally { _updateBusy = false; _updateDownloading = false; _updateOperation = null; operation.Dispose(); finished.TrySetResult(); RenderUpdates(); }
    }
    private async Task CancelUpdateForCloseAsync()
    { _updateOperation?.Cancel(); await _updateFinished; }
    private async Task CheckUpdatesAsync()
    {
        await RunUpdateAsync(async token =>
        {
            var client = new UpdateClient(new GitHubUpdateSource());
            _updateOffer = null; _downloadedUpdate = null;
            UpdateStatus("正在检查 GitHub Releases…");
            var offer = await client.CheckAsync(_updateCurrent, _updatePreferences.IncludePreviews, _updateRid, token);
            token.ThrowIfCancellationRequested(); if (_closed) return;
            _updateOffer = offer;
            UpdateStatus(offer?.IsNewer == true ? $"新版本 {offer.Release.Version} 可用。" : "当前渠道未发现更高版本。",
                offer is null ? "尚无可用于当前版本与架构的 Release。" : $"{offer.Package.Name} · {offer.Package.Size / 1048576.0:F1} MiB" + (offer.IsNewer ? "" : "\n可下载当前版本安装包以重新安装。"));
            if (_updatePreferences.ShouldDownload(offer)) await DownloadUpdateCoreAsync(client, offer!, token);
        });
    }
    private Task DownloadUpdateAsync() => RunUpdateAsync(async token =>
    { if (_updateOffer is { } offer) await DownloadUpdateCoreAsync(new(new GitHubUpdateSource()), offer, token); });
    private async Task DownloadUpdateCoreAsync(UpdateClient client, UpdateOffer offer, CancellationToken token)
    {
        _downloadedUpdate = null; _updateDownloading = true; UpdateStatus($"正在后台下载 {offer.Release.Version}…", "可继续浏览仓库；隐藏更新面板不会中断下载。");
        var sequence = _updateSequence; long last = 0;
        var downloaded = await client.DownloadAsync(offer, UpdateCache, bytes =>
        {
            var now = Environment.TickCount64;
            if (bytes < offer.Package.Size && now - Interlocked.Read(ref last) < 100) return;
            Interlocked.Exchange(ref last, now);
            Dispatcher.UIThread.Post(() => { if (!_closed && _updateBusy && sequence == _updateSequence && !token.IsCancellationRequested) _updatesPanel.Progress(bytes, offer.Package.Size); });
        }, token);
        token.ThrowIfCancellationRequested();
        await UpdateInstaller.MarkDownloadedAsync(downloaded.Path, token);
        token.ThrowIfCancellationRequested(); if (_closed) return;
        _downloadedUpdate = downloaded;
        UpdateStatus($"{offer.Release.Version} 已下载并通过双重 SHA-256 校验。", offer.Package.Name + "\n安装前会重新校验文件、检查系统签名并请求确认。下载完成不会自动安装或退出 Grove。");
    }
    private bool UpdateInstallBlocked()
    {
        if (!_model.IsWriting && _mergeWindow is null && !_commentBusy && _commentDraft is null) return false;
        UpdateStatus("请先完成 Git 操作并保存或关闭合并、批注编辑，再安装更新。", error: true); return true;
    }
    private Task InstallUpdateAsync() => RunUpdateAsync(async token =>
    {
        if (_downloadedUpdate is not { } downloaded || UpdateInstallBlocked()) return;
        UpdateStatus("正在重新校验安装包和签名…");
        await UpdateClient.VerifyFileAsync(downloaded, UpdateCache, token);
        await UpdateInstaller.MarkDownloadedAsync(downloaded.Path, token);
        var inspection = await UpdateInstaller.InspectAsync(downloaded.Path, token);
        token.ThrowIfCancellationRequested(); if (_closed || UpdateInstallBlocked()) return;
        var accepted = await ConfirmDialog.ShowAsync(this, "安装 Grove " + downloaded.Offer.Release.Version,
            downloaded.Offer.Package.Name + "\n\n" + inspection.Description + "\n\n确认后仅打开系统安装界面。Grove 不会自动关闭窗口或终止 Git 操作，请按安装提示手动退出应用。", OperatingSystem.IsMacOS() ? "打开 DMG" : "打开安装程序");
        token.ThrowIfCancellationRequested(); if (_closed) return;
        if (!accepted) { UpdateStatus("已取消安装。", "校验通过的下载仍在缓存中，可稍后再次安装。"); return; }
        if (UpdateInstallBlocked()) return;
        await UpdateClient.VerifyFileAsync(downloaded, UpdateCache, token);
        token.ThrowIfCancellationRequested();
        UpdateInstaller.OpenInteractive(downloaded.Path);
        UpdateStatus("已交给系统安装界面。", "请按系统提示操作；需要替换运行中的文件时，请自行正常退出 Grove。");
    });
}
