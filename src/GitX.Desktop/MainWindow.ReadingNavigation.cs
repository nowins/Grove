using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private bool _readingCommandPending;
    private async Task<ReadingPosition?> CaptureReadingPositionAsync(long version)
    {
        if (_closed || !_ready || _webView is null || _publishedVersion != version) return null;
        var repository = _model.RepositoryGeneration;
        try
        {
            var result = await _webView.InvokeScript(WebViewScript.Json($"window.gitx.readingPosition({version})"))
                .WaitAsync(TimeSpan.FromMilliseconds(250));
            if (_closed || repository != _model.RepositoryGeneration || version != _model.DocumentVersion || version != _publishedVersion) return null;
            var json = WebViewScript.DecodeObject(result, 2048);
            if (json is null) return null;
            var position = JsonSerializer.Deserialize<ReadingPosition>(json, TextJson);
            return position?.Version == version && position.Side is 0 or 1 && position.Line > 0 && position.Column > 0 ? position : null;
        }
        catch (Exception) { return null; }
    }
    private async Task NavigateReadingAsync(int direction, bool legacyLog = false)
    {
        if (_closed || _readingCommandPending || !IsActive) return;
        var command = direction < 0 ? ShellCommandId.ReadBack : ShellCommandId.ReadForward;
        if (!CanRunShellCommand(command, this))
        {
            if (legacyLog && direction < 0 && !_model.CanReadBack && _model.IsHistory) NavigateHistoryBack();
            return;
        }
        _readingCommandPending = true;
        try
        {
            if (await _model.NavigateReadingAsync(direction, _model.DocumentVersion)) RequestSourceFocus();
        }
        finally { _readingCommandPending = false; }
    }
    private bool ReadingKeyDown(KeyEventArgs key)
    {
        var modifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta | KeyModifiers.Alt : KeyModifiers.Alt;
        if (key.KeyModifiers != modifiers || key.Key is not (Key.Left or Key.Right)) return false;
        if (ReadingCompositionActive()) return false;
        key.Handled = true; _ = NavigateReadingAsync(key.Key == Key.Left ? -1 : 1, legacyLog: true); return true;
    }
    private bool ReadingCompositionActive()
    {
        var input = (FocusManager?.GetFocusedElement() as Control)?.GetSelfAndVisualAncestors().OfType<TextBox>().FirstOrDefault();
        return input?.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText)) == true;
    }
}
