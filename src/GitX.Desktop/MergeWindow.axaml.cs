using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MergeWindow : Window
{
    private readonly string _root, _path, _session = Guid.NewGuid().ToString("N");
    private readonly ConflictWriter _writer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _layoutRead;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private ConflictDocument? _document;
    private EditorServer? _server;
    private NativeWebView? _webView;
    private string _expected = "";
    private bool _ready, _closed, _busy, _dirty, _edited, _allowClose, _confirming, _canUndo;
    private bool _findPending, _tabMovesFocus, _canAutomatic;
    private int _remaining, _conflicts, _revision, _selected = -1;
    public bool Resolved { get; private set; }

    public MergeWindow() : this("", "") { }
    public MergeWindow(string root, string path)
    {
        _root = root; _path = path;
        InitializeComponent(); Title = $"合并修订：{path} — Grove";
        Opened += async (_, _) => await LoadAsync();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F7) { e.Handled = true; _webView?.Focus(); _ = CommandAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "previous" : "next"); }
            else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                if (!_ready) _findPending = true;
                else { _webView?.Focus(); _ = CommandAsync("find"); }
            }
            else if (e.Key == Key.M && e.KeyModifiers == KeyModifiers.Control && _ready)
            { e.Handled = true; _webView?.Focus(); _ = CommandAsync("toggle-tab"); }
        }, RoutingStrategies.Tunnel);
        Closing += async (_, e) =>
        {
            if (_busy) { e.Cancel = true; Error("正在写入，请等待完成后关闭。"); return; }
            if (_edited && !_allowClose)
            {
                e.Cancel = true;
                if (_confirming) return;
                _confirming = true;
                if (await ConfirmDialog.ShowAsync(this, "关闭合并窗口", "关闭将放弃本窗口未保存的编辑。\n已经保存的工作区内容不会撤销。", "放弃未保存内容并关闭"))
                { _allowClose = true; Close(); }
                _confirming = false;
            }
        };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _layoutRead?.Cancel(); _layoutRead?.Dispose(); _lifetime.Dispose();
            MergeHost.Content = null; _webView = null; _server?.Dispose();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            _document = await new ConflictReader().ReadAsync(_root, _path, _lifetime.Token);
            _document = await ConflictReader.PrepareSessionAsync(_document, _lifetime.Token);
            if (_closed) return;
            _expected = _document.Token; _remaining = _document.Blocks.Count;
            DetailLabel.Text = $"{_document.Operation?.Label ?? "索引冲突"} · {_document.Operation?.Detail} · {_document.LineEnding}";
            if (!_document.IsText)
            {
                SpecialPanel.IsVisible = true; AutomaticButton.IsEnabled = false;
                SpecialTitle.Text = _path; SpecialDescription.Text = _document.Limitation;
                SideDescription.Text = $"{_document.Left.Label}：{_document.Left.Path} · {_document.Left.Sha ?? "删除"}\n{_document.Right.Label}：{_document.Right.Path} · {_document.Right.Sha ?? "删除"}";
                UseLeftButton.Content = _document.Left.Exists ? "采用完整左侧" : "采用左侧（删除）";
                UseRightButton.Content = _document.Right.Exists ? "采用完整右侧" : "采用右侧（删除）";
                UpdateState(); return;
            }
            var profile = Environment.GetEnvironmentVariable("GITX_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitX", "WebView2");
            Directory.CreateDirectory(profile);
            _server = new EditorServer("merge.html");
            _server.Publish(await Task.Run(() => JsonSerializer.SerializeToUtf8Bytes(new { session = _session, document = _document }, _json)));
            if (_closed) return;
            _webView = new NativeWebView();
            _webView.EnvironmentRequested += (_, e) => { e.EnableDevTools = false; if (e is WindowsWebView2EnvironmentRequestedEventArgs windows) windows.UserDataFolder = profile; };
            _webView.NavigationStarted += (_, e) => { if (!_server.Owns(e.Request)) e.Cancel = true; };
            _webView.NewWindowRequested += (_, e) => e.Handled = true;
            _webView.WebMessageReceived += (_, e) => Dispatcher.UIThread.Post(() => Message(e.Body));
            _webView.Source = _server.Address; MergeHost.Content = _webView;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed) Error(e.Message); }
    }

    private async void Message(string? body)
    {
        if (_closed || body is null) return;
        try
        {
            using var parsed = JsonDocument.Parse(body); var message = parsed.RootElement;
            if (message.GetProperty("session").GetString() != _session) return;
            switch (message.GetProperty("type").GetString())
            {
                case "ready":
                    _ready = true;
                    if (_findPending) { _findPending = false; _webView?.Focus(); await CommandAsync("find"); }
                    break;
                case "focus-out":
                    if (_webView is not null && message.TryGetProperty("direction", out var direction) && direction.TryGetInt32(out var tabDirection) && tabDirection is -1 or 1)
                        NativeKeyboardFocus.LeaveEmbedded(this, _webView, tabDirection);
                    break;
                case "state":
                    _revision = message.GetProperty("revision").GetInt32(); _dirty = message.GetProperty("dirty").GetBoolean(); _remaining = message.GetProperty("remaining").GetInt32();
                    _edited = message.GetProperty("edited").GetBoolean();
                    _conflicts = message.GetProperty("conflicts").GetInt32(); _canAutomatic = message.GetProperty("canAutomatic").GetBoolean();
                    _tabMovesFocus = message.GetProperty("tabMovesFocus").GetBoolean();
                    _selected = message.GetProperty("selected").GetInt32(); _canUndo = message.GetProperty("canUndo").GetBoolean(); UpdateState(); break;
                case "layout":
                    var revision = message.GetProperty("revision").GetInt32(); var text = message.GetProperty("text").GetString()!;
                    _layoutRead?.Cancel(); _layoutRead?.Dispose(); _layoutRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    var token = _layoutRead.Token;
                    var layout = await ConflictReader.LayoutAsync(_document!.Left.Text!, text, _document.Right.Text!, token);
                    var payload = await Task.Run(() => JsonSerializer.SerializeToUtf8Bytes(new { session = _session, revision, layout }, _json), token);
                    if (!_closed && !token.IsCancellationRequested && revision == _revision) { _server!.PublishLayout(payload); await InvokeAsync($"window.gitxMerge.layout({revision})"); }
                    break;
                case "save":
                    if (_remaining != 0) { Error("请先逐处确认冲突，再保存结果。"); break; }
                    var saveRevision = message.GetProperty("revision").GetInt32(); var result = message.GetProperty("text").GetString()!;
                    await WriteAsync(async () => { _expected = await _writer.SaveAsync(_document!, _expected, result); await InvokeAsync($"window.gitxMerge.saved({saveRevision})"); });
                    break;
                case "resolve": if (!_dirty && _remaining == 0) await ResolveAsync(); break;
                case "apply":
                    if (!_busy && message.GetProperty("revision").GetInt32() == _revision)
                        await ApplyAsync(_revision, message.GetProperty("text").GetString()!);
                    break;
                case "compare":
                    if (!_busy && message.GetProperty("revision").GetInt32() == _revision)
                        await CompareAsync(message.GetProperty("pair").GetString()!, message.GetProperty("text").GetString()!);
                    break;
                case "error": Error(message.GetProperty("message").GetString()!); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_closed) Error(e.Message); }
    }

    private async Task WriteAsync(Func<Task> action)
    {
        if (_closed || _busy || _document is null) return;
        _busy = true; ErrorPanel.IsVisible = false; UpdateState(); await InvokeAsync("window.gitxMerge.busy(true)");
        try { await action(); }
        catch (Exception e) { Error(e.Message); }
        finally { _busy = false; await InvokeAsync("window.gitxMerge.busy(false)"); UpdateState(); }
    }
    private async Task ResolveAsync()
    {
        var succeeded = false;
        await WriteAsync(async () => { await _writer.ResolveAsync(_document!, _expected); succeeded = true; });
        if (succeeded) { Resolved = true; _dirty = false; _allowClose = true; Close(); }
    }
    private async Task ApplyAsync(int revision, string text)
    {
        if (_confirming || _document?.IsText != true) return;
        if (_remaining > 0)
        {
            _confirming = true;
            await InvokeAsync("window.gitxMerge.busy(true)");
            bool accepted;
            var clean = _remaining - _conflicts;
            var consequence = "将仅保存中间结果区当前显示的文本，不会自动应用剩余改动。";
            if (clean > 0) consequence += "\n尚未应用到中间结果的非冲突改动不会进入最终结果；未编辑的区域仍为共同祖先内容。";
            if (_conflicts > 0) consequence += "\n未处理冲突也将按中间结果当前文本保存，并将文件标记为已解决。";
            var nextStep = clean > 0 ? "要保留非冲突改动，请取消并先使用“应用非冲突改动”，再核对结果。" : "要继续处理冲突，请取消并返回合并窗口。";
            try
            {
                accepted = await ConfirmDialog.ShowAsync(this, "仍有未处理改动",
                    $"{_path}\n\n还剩 {clean} 处非冲突改动、{_conflicts} 处冲突未处理。\n\n{consequence}\n\n{nextStep}\n\n仍要按当前结果保存并将文件标记为已解决吗？",
                    "按当前结果保存并解决");
            }
            finally { _confirming = false; await InvokeAsync("window.gitxMerge.busy(false)"); }
            if (!accepted || revision != _revision) return;
        }
        var succeeded = false;
        await WriteAsync(async () =>
        {
            _expected = await _writer.SaveAsync(_document, _expected, text);
            await InvokeAsync($"window.gitxMerge.saved({revision})");
            await _writer.ResolveAsync(_document, _expected); succeeded = true;
        });
        if (succeeded) { Resolved = true; _dirty = _edited = false; _allowClose = true; Close(); }
    }

    private void CompareClicked(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var (pair, label) in new[] { ("left-result", "当前版本 ↔ 合并结果"), ("right-result", "传入版本 ↔ 合并结果"), ("left-right", "当前版本 ↔ 传入版本"), ("base-left", "共同祖先 ↔ 当前版本"), ("base-result", "共同祖先 ↔ 合并结果"), ("base-right", "共同祖先 ↔ 传入版本") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => { menu.Hide(); await CommandAsync("compare-" + pair); };
            menu.Items.Add(item);
        }
        menu.ShowAt(CompareButton);
    }
    private async Task CompareAsync(string pair, string text)
    {
        if (_document is null) return;
        DiffSide Side(string key) => key == "result" ? new(_path, "合并结果", text, true) :
            key == "base" ? new(_path, "共同祖先", _document.Base.Text!, _document.Base.Exists) :
            key == "left" ? new(_path, _document.Left.Label, _document.Left.Text!, _document.Left.Exists) :
            key == "right" ? new(_path, _document.Right.Label, _document.Right.Text!, _document.Right.Exists) : throw new IOException("比较对象无效。");
        var sides = pair.Split('-'); if (sides.Length != 2) return;
        await new DiffWindow(_path, Side(sides[0]), Side(sides[1])).ShowDialog(this);
    }
    private void UpdateState()
    {
        PreviousButton.IsEnabled = NextButton.IsEnabled = !_busy && _ready && _remaining > 0;
        UndoButton.IsEnabled = !_busy && _ready && _canUndo;
        AcceptLeftButton.IsEnabled = AcceptRightButton.IsEnabled = AcceptBothButton.IsEnabled = KeepResultButton.IsEnabled = !_busy && _ready && _selected >= 0;
        AutomaticButton.IsEnabled = !_busy && _ready && _canAutomatic;
        CompareButton.IsEnabled = !_busy && _ready;
        StateLabel.Text = _busy ? "正在写入…" : _remaining == 0 ? (_document?.ExistingResult == true ? "已载入工作区结果" : "全部改动已处理") : $"剩余 {_remaining - _conflicts} 处改动、{_conflicts} 处冲突";
        StateLabel.Classes.Set("complete", !_busy && _remaining == 0);
        if (_document?.IsText == true)
            DetailLabel.Text = (_tabMovesFocus ? "Tab：切换焦点 · Ctrl+M 恢复缩进" : "Tab：缩进 · Ctrl+M 切换焦点导航") + $" · {_document.Operation?.Label ?? "索引冲突"} · {_document.LineEnding}";
        SaveStateLabel.Text = _busy ? "正在写入，请等待完成…" : _dirty ? (_edited ? "有未保存的编辑 · 保存结果只写入工作区" : "自动合并候选尚未保存 · 请先确认冲突") : "工作区结果已保存 · 尚未标记解决";
        SaveButton.IsEnabled = !_busy && _ready && _document?.IsText == true && _dirty && _remaining == 0;
        ResolveButton.IsEnabled = !_busy && _document is not null && !_dirty && _remaining == 0;
        ApplyButton.IsEnabled = !_busy && _ready && _document?.IsText == true;
        UseLeftButton.IsEnabled = UseRightButton.IsEnabled = !_busy;
    }
    private void Error(string message) { ErrorText.Text = message; ErrorPanel.IsVisible = true; }
    private async Task InvokeAsync(string script)
    {
        if (_closed || _webView is null || !_ready) return;
        try { await _webView.InvokeCommandAsync(script); } catch (Exception e) { if (!_closed) Error(e.Message); }
    }
    private Task CommandAsync(string command) => InvokeAsync($"window.gitxMerge.command({JsonSerializer.Serialize(command)})");
    private async void EditorCommandClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command }) { _webView?.Focus(); await CommandAsync(command); }
    }
    private async void ResolveClicked(object? sender, RoutedEventArgs e) { if (_document?.IsText == true) await CommandAsync("resolve"); else await ResolveAsync(); }
    private async Task ChooseSideAsync(bool left)
    {
        if (_document is null) return;
        if (await ConfirmDialog.ShowAsync(this, "采用完整一侧", $"{_path}\n\n将工作区替换为{(left ? _document.Left.Label : _document.Right.Label)}。缺失的一侧表示删除，当前文件会先备份；索引仍保持冲突，需另行标记解决。", "采用此侧并保留备份"))
            await WriteAsync(async () => _expected = await _writer.ChooseSideAsync(_document, _expected, left));
    }
    private async void UseLeftClicked(object? sender, RoutedEventArgs e) => await ChooseSideAsync(true);
    private async void UseRightClicked(object? sender, RoutedEventArgs e) => await ChooseSideAsync(false);
    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
