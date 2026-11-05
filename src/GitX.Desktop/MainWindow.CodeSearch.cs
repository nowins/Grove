using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

public sealed partial class MainWindow
{
    private CodeSearchWindow? _codeSearch;
    private string? _codeSearchRoot;
    private CodeSearchQuery _codeSearchQuery = new("");
    private readonly DoubleShiftGesture _doubleShift = new();

    private bool DoubleShiftAllowed()
    {
        if (_closed || !InteractionWindow.IsActive || _codeSearch is not null || _model.Root is null) return false;
        var input = (InteractionWindow.FocusManager?.GetFocusedElement() as Control)?.GetSelfAndVisualAncestors().OfType<TextBox>().FirstOrDefault();
        return input is null || !input.AcceptsReturn && !input.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));
    }
    private void DoubleShiftDown(object? sender, KeyEventArgs e) => _doubleShift.Down(e.Key is Key.LeftShift or Key.RightShift,
        DoubleShiftAllowed() && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None, Environment.TickCount64);
    private void DoubleShiftUp(object? sender, KeyEventArgs e)
    {
        if (_doubleShift.Up(e.Key is Key.LeftShift or Key.RightShift,
            DoubleShiftAllowed() && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None, Environment.TickCount64))
        { e.Handled = true; _ = OpenCodeSearchAsync(true, quick: true); }
    }

    private async void FindCodeClicked(object? sender, RoutedEventArgs e) => await OpenCodeSearchAsync(false);
    private async void FindFileClicked(object? sender, RoutedEventArgs e) => await OpenCodeSearchAsync(true);

    private Task OpenSourceSymbolsAsync()
    {
        if (_closed || !_ready || _model.Mode != 0 || _model.CurrentDocument is not { IsNotice: false } document || !SourceLanguage.SupportsStructure(document.Language)) return Task.CompletedTask;
        _webView?.Focus();
        return InvokeEditorAsync("window.gitx.openSymbols()");
    }

    private async Task OpenCodeSearchAsync(bool filesOnly, bool quick = false, Action? restoreEmbeddedFocus = null)
    {
        if (_closed || _model.Root is not { } root) return;
        if (_codeSearch is not null) { _codeSearch.ActivateSearch(); return; }
        var searchOwner = InteractionWindow;
        var previousFocus = searchOwner.FocusManager?.GetFocusedElement() as Control;
        var editorFocused = _webView?.IsFocused == true || NativeKeyboardFocus.HasEmbeddedFocus(searchOwner);
        if (_codeSearchRoot != root) { _codeSearchQuery = new(""); _codeSearchRoot = root; }
        var commandVersion = _model.DocumentVersion;
        bool ContextCurrent() => !_closed && _model.Root == root && _model.DocumentVersion == commandVersion;
        bool CanRunAction(ShellCommandId id) => ContextCurrent() && CanRunShellCommand(id, searchOwner);
        var window = new CodeSearchWindow(root, () => _model.RepositoryPaths, _model.RepositoryFilesTruncated, _codeSearchQuery with { FilesOnly = filesOnly }, async match =>
        {
            if (_closed || _model.Root != root) return "仓库已变化，请重新搜索。";
            var opened = await _model.OpenCodeSearchMatchAsync(root, match);
            UpdateNavigationLayout();
            return opened ? null : string.IsNullOrEmpty(_model.Error) ? "此结果已失效，请重新搜索。" : _model.Error;
        }, quick, CanRunAction, ContextCurrent);
        void ContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => window.InvalidatePreviewContext();
        _model.PropertyChanged += ContextChanged;
        _codeSearch = window;
        try { if (quick) await window.ShowQuickAsync(searchOwner); else await window.ShowDialog(searchOwner); }
        finally { _model.PropertyChanged -= ContextChanged; _codeSearchQuery = window.Query; _codeSearch = null; }
        if (window.RequestedCommand is { } command)
        {
            // The popup is gone and _codeSearch cleared before commands can open
            // another search/dialog. Do not queue the old editor-focus restore.
            if (CanRunAction(command)) { searchOwner.Activate(); RunShellCommand(command, searchOwner); }
            return;
        }
        if (window.OpenDetailedRequested) { await OpenCodeSearchAsync(false, restoreEmbeddedFocus: restoreEmbeddedFocus); return; }
        var version = _model.DocumentVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !searchOwner.IsActive || !window.RestoreFocusOnClose || _model.Root != root || version != _model.DocumentVersion) return;
            if (window.OpenedMatch) { Activate(); RequestSourceFocus(); }
            else if (restoreEmbeddedFocus is not null) restoreEmbeddedFocus();
            else if (editorFocused && _model.Mode == 0) RequestSourceFocus();
            else if (editorFocused)
            {
                FocusManager?.Focus(null);
                _webView?.Focus(); _ = InvokeEditorAsync("window.gitx.focus()");
            }
            else if (previousFocus is { IsVisible: true, IsEnabled: true }) NativeKeyboardFocus.TryFocusControl(this, previousFocus);
        }, DispatcherPriority.Background);
    }
}
