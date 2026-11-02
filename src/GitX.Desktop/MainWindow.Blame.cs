using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _blameRead;
    private readonly BlameViewStore _blameViewStore = new(LocalDataDirectory);
    private BlameViewSettings _blameView = new();
    private Task _blameViewReady = Task.CompletedTask;
    private long _blameViewSequence;

    private async Task LoadBlameViewAsync()
    {
        try { _blameView = await _blameViewStore.ReadAsync(); }
        catch (Exception ex) { if (!_closed) _model.ReportError("注解显示设置读取失败：" + ex.Message); }
    }

    private async Task HandleBlameViewAsync(string field, bool value, long version)
    {
        bool Current() => !_closed && version == _publishedVersion && version == _model.DocumentVersion && _model.CurrentDocument?.Blame is not null;
        if (!Current()) return;
        var owner = ++_blameViewSequence;
        try
        {
            await _blameViewReady;
            if (!Current()) return;
            _blameView = _blameView.Change(field, value);
            var saved = await _blameViewStore.UpdateAsync(field, value);
            if (!Current() || owner != _blameViewSequence) return;
            _blameView = saved;
            await InvokeEditorAsync("window.gitx.setBlameView(" + JsonSerializer.Serialize(saved, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + ")");
        }
        catch (Exception ex)
        {
            if (!Current() || owner != _blameViewSequence) return;
            _model.ReportError("注解显示设置未能保存：" + ex.Message);
            if (owner == _blameViewSequence)
            {
                var restored = _blameView;
                try { restored = await _blameViewStore.ReadAsync(); } catch (IOException) { }
                if (!Current() || owner != _blameViewSequence) return;
                _blameView = restored;
                await InvokeEditorAsync("window.gitx.setBlameView(" + JsonSerializer.Serialize(_blameView, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + ")");
            }
        }
    }

    private async Task HandleBlameActionAsync(string action, int line, long version)
    {
        if (version != _publishedVersion || version != _model.DocumentVersion ||
            _model.Root is not { } root || _model.BlameLineAt(line, version) is not { } info || _model.CurrentDocument is not { } source) return;
        bool Current() => !_closed && root == _model.Root && version == _publishedVersion && version == _model.DocumentVersion;
        try
        {
            switch (action)
            {
                case "close": await _model.ToggleBlameAsync(); break;
                case "previous" when info.HasPrevious: await _model.PreviousAnnotationAsync(line, version); break;
                case "copy" when info.IsCommitted:
                    var clipboard = GetTopLevel(this)?.Clipboard ?? throw new IOException("系统剪贴板不可用。");
                    await clipboard.SetTextAsync(info.Sha); break;
                case "diff" when info.IsCommitted:
                    _blameRead?.Cancel(); _blameRead?.Dispose(); _blameRead = new CancellationTokenSource();
                    var document = await new BlameReader().ReadChangeAsync(root, info, _blameRead.Token);
                    if (!Current()) return;
                    if (document.Diff is not { } diff) throw new IOException(document.Text);
                    new DiffWindow(document.Path, diff.Old, diff.New).Show(this);
                    break;
                case "history":
                    await OpenSourceHistoryAsync(version);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current()) _model.ReportError(ex.Message); }
    }
}
