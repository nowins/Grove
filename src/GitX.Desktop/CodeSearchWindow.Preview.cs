using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

internal sealed partial class CodeSearchWindow
{
    private const double PreviewMinimumWidth = 960;
    private readonly SearchPreviewSession _previewSession = new();
    private Func<bool> _previewContextCurrent = () => true;
    private Window? _previewOwner;
    private Grid? _quickBody;
    private Border? _previewPane;
    private Button? _previewToggle, _previewRetry;
    private TextBlock? _previewPath, _previewStatus;
    private TextBox? _previewText;
    private bool _previewRequested, _previewVisible;

    private Control BuildQuickPreview(Control results)
    {
        _quickBody = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,0") };
        _quickBody.Children.Add(results);
        var content = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new(12, 8) };
        _previewPath = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.Medium };
        content.Children.Add(_previewPath);
        var source = new TextBlock { Text = "工作区磁盘 · 只读预览\n不含未保存编辑", Foreground = Brush("#8B8E96"), FontSize = UiTypography.SecondarySize };
        Grid.SetRow(source, 1); content.Children.Add(source);
        _previewText = new TextBox
        {
            IsReadOnly = true, IsUndoEnabled = false, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = (FontFamily)Application.Current!.FindResource("CodeFontFamily")!, FontSize = UiTypography.SecondarySize,
            BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_previewText, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_previewText, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_previewText, "工作区磁盘只读预览，不含未保存编辑");
        Grid.SetRow(_previewText, 2); content.Children.Add(_previewText);
        var footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
        _previewStatus = new TextBlock { Foreground = Brush("#8B8E96"), TextWrapping = TextWrapping.Wrap, FontSize = UiTypography.SecondarySize };
        footer.Children.Add(_previewStatus);
        _previewRetry = QuickButton("重试", 44); _previewRetry.IsVisible = false;
        _previewRetry.Click += (_, _) => { _query.Focus(); QueuePreview(); };
        Grid.SetColumn(_previewRetry, 1); footer.Children.Add(_previewRetry);
        Grid.SetRow(footer, 3); content.Children.Add(footer);
        _previewPane = new Border { Child = content, BorderThickness = new(1, 0, 0, 0), BorderBrush = Brush("#393B40"), IsVisible = false };
        Grid.SetColumn(_previewPane, 1); _quickBody.Children.Add(_previewPane);
        return _quickBody;
    }

    private void ConfigurePreviewOwner(Window owner)
    {
        _previewOwner = owner;
        owner.PropertyChanged += PreviewOwnerChanged;
        UpdatePreviewLayout();
        Closed += (_, _) =>
        {
            owner.PropertyChanged -= PreviewOwnerChanged;
            _previewOwner = null; _previewSession.Dispose(); ClearPreview();
        };
    }

    private void PreviewOwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty || e.Property == ClientSizeProperty) UpdatePreviewLayout();
    }

    private void UpdatePreviewLayout()
    {
        if (_previewOwner is not { } owner || _quickBody is null || _closed || _closing) return;
        var available = Math.Max(0, owner.ClientSize.Width - 32);
        var wide = available >= PreviewMinimumWidth;
        var visible = _previewRequested && wide && !IsActionSearch;
        if (_previewToggle is not null)
        {
            _previewToggle.IsEnabled = wide && !IsActionSearch;
            _previewToggle.Content = visible ? "隐藏预览" : "预览";
            ToolTip.SetTip(_previewToggle, !wide ? "窗口较窄，请扩大主窗口后预览" : IsActionSearch ? "操作不提供文件预览" : "显示工作区磁盘的只读文本；不会打开标签");
            ToolTip.SetShowOnDisabled(_previewToggle, true);
        }
        var changed = visible != _previewVisible;
        _previewVisible = visible;
        if (!visible && _previewPane?.IsKeyboardFocusWithin == true) _query.Focus();
        _quickBody.ColumnDefinitions[1].Width = visible ? new GridLength(380) : new GridLength(0);
        if (_previewPane is not null) _previewPane.IsVisible = visible;
        Width = Math.Min(visible ? 1050 : 670, available);
        Height = Math.Min(685, Math.Max(0, owner.ClientSize.Height - 32));
        Position = owner.PointToScreen(new Point((owner.ClientSize.Width - Width) / 2,
            Math.Min(124, Math.Max(16, owner.ClientSize.Height - Height - 16))));
        if (!visible) CancelPreview();
        else if (changed) QueuePreview();
    }

    public void InvalidatePreviewContext()
    {
        if (!_quick || _closed || _previewContextCurrent()) return;
        CancelPreview();
        if (_previewVisible && _previewStatus is not null) _previewStatus.Text = "仓库或文档已变化，请关闭后重新搜索。";
    }

    private void ClearPreview()
    {
        if (_previewText is not null) _previewText.Text = "";
        if (_previewPath is not null) { _previewPath.Text = ""; ToolTip.SetTip(_previewPath, null); }
        if (_previewStatus is not null) _previewStatus.Text = "选择文件结果以预览";
        if (_previewRetry is not null) _previewRetry.IsVisible = false;
    }

    private void CancelPreview() { _previewSession.Invalidate(); ClearPreview(); }

    private void QueuePreview()
    {
        CancelPreview();
        if (!_quick || !_previewVisible || _closed || _closing || _results.SelectedItem is not CodeSearchMatch match) return;
        _previewPath!.Text = match.Path; ToolTip.SetTip(_previewPath, match.Path);
        _previewStatus!.Text = "正在读取只读预览…";
        _ = LoadPreviewAsync(match);
    }

    private async Task LoadPreviewAsync(CodeSearchMatch match)
    {
        if (!await _previewSession.LoadAsync(_root, match, _previewContextCurrent)) return;
        if (_closed || _closing || !_previewVisible || !Equals(_results.SelectedItem, match)) return;
        if (_previewSession.Current is { } preview)
        {
            _previewText!.Text = preview.Text;
            var caret = 0;
            for (var line = preview.FirstLine; line < preview.SelectedLine; line++) caret = preview.Text.IndexOf('\n', caret) + 1;
            // Scroll the native read-only presenter to the selected line without
            // moving keyboard focus away from the search query.
            _previewText.CaretIndex = caret;
            _previewText.SelectionStart = _previewText.SelectionEnd = caret;
            _previewStatus!.Text = $"第 {preview.SelectedLine:N0} 行 / 共 {preview.TotalLines:N0} 行" +
                (preview.Truncated ? " · 有界上下文，› 标记选中行" : " · › 标记选中行");
        }
        else
        {
            _previewStatus!.Text = _previewSession.Error ?? "预览已取消";
            _previewRetry!.IsVisible = _previewContextCurrent();
        }
    }
}
