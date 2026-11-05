using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

// Snapshot comparison has its own origin and WebView, leaving the merge session intact.
internal sealed class DiffWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ContentControl _host = new() { Margin = new Thickness(4, 0), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
    private readonly TextBlock _error = new() { Margin = new Thickness(12), TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private EditorServer? _server;
    private NativeWebView? _view;
    private long _version;
    private bool _ready;
    public DiffWindow(string path, DiffSide left, DiffSide right)
    {
        Title = $"{left.Label} ↔ {right.Label}：{path}"; Width = 1300; Height = 800; MinWidth = 800; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }; grid.Children.Add(_error); Grid.SetRow(_host, 1); grid.Children.Add(_host); Content = grid; DialogChrome.Apply(this);
        Opened += async (_, _) =>
        {
            try
            {
                _server = new EditorServer(); _view = new NativeWebView();
                DesktopWebView.Configure(_view);
                _view.NavigationStarted += (_, e) => { if (!_server.Owns(e.Request)) e.Cancel = true; };
                _view.NewWindowRequested += (_, e) => e.Handled = true;
                _view.WebMessageReceived += (_, e) => Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        using var parsed = JsonDocument.Parse(e.Body!); var message = parsed.RootElement;
                        switch (message.GetProperty("type").GetString())
                        {
                            case "ready": _ready = true; await _view.InvokeCommandAsync($"window.gitx.load({_version})"); break;
                            case "focus-navigation": case "close-document": Close(); break;
                            case "whitespace": await PublishAsync((DiffWhitespace)message.GetProperty("value").GetInt32()); break;
                            case "focus-out": NativeKeyboardFocus.LeaveEmbedded(this, _view, message.GetProperty("direction").GetInt32()); break;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _error.Text = ex.Message; _error.IsVisible = true; }
                });
                await PublishAsync(DiffWhitespace.Trim);
                if (_lifetime.IsCancellationRequested) return;
                _view.Source = _server.Address; _host.Content = _view;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _error.Text = ex.Message; _error.IsVisible = true; }
        };
        async Task PublishAsync(DiffWhitespace whitespace)
        {
            if (!Enum.IsDefined(whitespace)) return;
            var version = ++_version;
            var compared = await Task.Run(() => NativeDiff.CompareAsync(left, right, _lifetime.Token, whitespace), _lifetime.Token);
            var language = SourceLanguage.Identify(path);
            var document = new RepositoryDocument(path, compared.Patch, language, left.Label + " → " + right.Label, $"{compared.Diff.Blocks.Count} 处差异", IsDiff: true, Diff: compared.Diff);
            var payload = await Task.Run(() => EditorServer.Serialize(version, document, new(path, false, "注解")), _lifetime.Token);
            if (_lifetime.IsCancellationRequested || version != _version) return;
            _server!.Publish(payload); if (_ready) await _view!.InvokeCommandAsync($"window.gitx.load({version})");
        }
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.F7 && _ready) { e.Handled = true; _view?.Focus(); _ = _view?.InvokeCommandAsync($"window.gitx.navigate({(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1)})"); }
        }, RoutingStrategies.Tunnel);
        Closed += (_, _) => { _lifetime.Cancel(); _host.Content = null; _view = null; _server?.Dispose(); _lifetime.Dispose(); };
    }
}
