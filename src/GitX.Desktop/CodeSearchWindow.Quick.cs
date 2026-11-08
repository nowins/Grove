using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop;

internal sealed partial class CodeSearchWindow
{
    // Dimensions and states are measured from the local IDEA screenshots.
    private enum QuickCategory { All, Files, Text, Actions }
    private QuickCategory _category;
    private readonly Dictionary<QuickCategory, Button> _categoryButtons = [];
    private readonly Dictionary<CodeSearchMatch, Run> _textHits = [];
    private TextBlock? _empty, _selectedPath, _commands;
    private StackPanel? _textScope;
    private Button? _fileTypes;
    private bool _closing, _dismissedOutside;
    public bool RestoreFocusOnClose => !_dismissedOutside;
    public bool OpenDetailedRequested { get; private set; }

    public async Task ShowQuickAsync(Window owner)
    {
        Width = Math.Min(670, owner.ClientSize.Width - 32);
        Height = Math.Min(685, owner.ClientSize.Height - 32);
        MinWidth = 0; MinHeight = 0;
        CanResize = CanMinimize = CanMaximize = false;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = owner.PointToScreen(new Point((owner.ClientSize.Width - Width) / 2,
            Math.Min(124, Math.Max(16, owner.ClientSize.Height - Height - 16))));
        ConfigurePreviewOwner(owner);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, EventArgs e) => completion.TrySetResult();
        Closed += OnClosed;
        try { Show(owner); await completion.Task; }
        finally { Closed -= OnClosed; }
    }

    public void ActivateSearch() { Activate(); if (_quick) _query.Focus(); }
    private void CloseSearch() { if (_opening) return; _closing = true; Close(); }

    private Control BuildQuickContent(Control options, Control scope)
    {
        var panel = new Grid { RowDefinitions = RowDefinitions.Parse("38,37,*,26") };
        var top = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"), Margin = new(5, 0, 8, 0) };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, width, category) in new (string, double, QuickCategory)[]
        {
            ("All", 36, QuickCategory.All), ("Files", 44, QuickCategory.Files),
            ("Text", 46, QuickCategory.Text), ("Actions", 64, QuickCategory.Actions)
        })
        {
            var button = QuickButton(label, width);
            _categoryButtons[category] = button; button.Click += (_, _) => SelectQuickCategory(category);
            tabs.Children.Add(button);
        }
        top.Children.Add(tabs);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _textScope = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _fileTypes = QuickButton(FileTypeContent("All file types"), 118);
        _fileTypes.Height = 22; _fileTypes.Margin = new(0, 4, 0, 0);
        _fileTypes.BorderThickness = new(1); _fileTypes.BorderBrush = Brush("#43454A");
        var maskPanel = new StackPanel { Spacing = 8, Width = 230 };
        maskPanel.Children.Add(new TextBlock { Text = "File mask" });
        ((Panel)scope).Children.Remove(_mask); maskPanel.Children.Add(_mask); maskPanel.Children.Add(options);
        var maskFlyout = new Flyout { Content = maskPanel }; maskFlyout.FlyoutPresenterClasses.Add("shell-popup");
        _fileTypes.Click += (_, _) => { maskFlyout.ShowAt(_fileTypes); _mask.Focus(); };
        _textScope.Children.Add(_fileTypes); tools.Children.Add(_textScope);
        _previewToggle = QuickButton("预览", 76);
        _previewToggle.Click += (_, _) => { _query.Focus(); _previewRequested = !_previewRequested; UpdatePreviewLayout(); };
        tools.Children.Add(_previewToggle);
        var find = QuickButton(QuickIcon("M3 3 H13 V13 H3 Z M10 5 L6 9 M6 6 V9 H9"), 24);
        ToolTip.SetTip(find, "Find in Files · " + ShellCommands.All.Single(c => c.Id == ShellCommandId.FindText).Gesture(OperatingSystem.IsMacOS()));
        find.Click += (_, _) => { OpenDetailedRequested = true; CloseSearch(); }; tools.Children.Add(find);
        Grid.SetColumn(tools, 2); top.Children.Add(tools);
        panel.Children.Add(new Border { Child = top, BorderThickness = new(0, 0, 0, 1), BorderBrush = Brush("#333438") });

        var input = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("16,*,Auto"), ColumnSpacing = 9, Margin = new(18, 0, 20, 0) };
        input.Children.Add(QuickIcon("M10.5 6.5 A4 4 0 1 1 2.5 6.5 A4 4 0 1 1 10.5 6.5 M9.5 9.5 L14 14"));
        _query.Name = "IdeaSearchQuery"; _query.BorderThickness = new(0); _query.Background = Brushes.Transparent; _query.CornerRadius = new(0);
        _query.Padding = new(0); _query.MinHeight = 0; _query.Height = 36; _query.VerticalContentAlignment = VerticalAlignment.Center;
        _query.SelectionBrush = Brush("#3E5C83"); _query.FontSize = UiTypography.Size;
        foreach (var state in new[] { "search", ":focus", ":pointerover" })
            _query.Styles.Add(new Style(x => x.OfType<TextBox>().Name("IdeaSearchQuery").Class(state).Template().OfType<Border>().Name("PART_BorderElement"))
            { Setters = { new Setter(Border.BorderThicknessProperty, new Thickness(0)), new Setter(Border.BackgroundProperty, Brushes.Transparent) } });
        Grid.SetColumn(_query, 1); input.Children.Add(_query);
        _commands = new TextBlock { Text = "Type / to see commands", Foreground = Brush("#6F737A"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_commands, 2); input.Children.Add(_commands);
        var inputBorder = new Border { Child = input, BorderThickness = new(0, 0, 0, 1), BorderBrush = Brush("#333438") };
        Grid.SetRow(inputBorder, 1); panel.Children.Add(inputBorder);

        _results.Name = "IdeaSearchResults"; _results.Background = Brushes.Transparent; _results.BorderThickness = new(0);
        _results.AutoScrollToSelectedItem = true; _results.Margin = new(7, 4, 7, 0); _results.Padding = new(0);
        _results.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            CodeSearchMatch match => QuickResultRow(match),
            ShellCommandDefinition command => QuickActionRow(command),
            _ => new Border()
        });
        _results.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HeightProperty, 22d), new Setter(ListBoxItem.MinHeightProperty, 22d), new Setter(ListBoxItem.PaddingProperty, new Thickness(9, 0)), new Setter(ListBoxItem.FocusAdornerProperty, null) } });
        _results.Styles.Add(new Style(x => x.OfType<ListBox>().Name("IdeaSearchResults").Descendant().OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
        { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brush("#2E436E")), new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(4)) } });
        _results.Styles.Add(new Style(x => x.OfType<TextBlock>().Class("idea-result-location"))
        { Setters = { new Setter(TextBlock.ForegroundProperty, Brush("#6F737A")) } });
        _results.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected").Descendant().OfType<TextBlock>().Class("idea-result-location"))
        { Setters = { new Setter(TextBlock.ForegroundProperty, Brush("#BCBEC4")) } });
        _results.AddHandler(PointerReleasedEvent, (_, _) => _query.Focus(), RoutingStrategies.Bubble, handledEventsToo: true);
        var resultArea = new Grid(); resultArea.Children.Add(_results);
        _empty = new TextBlock { Foreground = Brush("#6F737A"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        resultArea.Children.Add(_empty); var body = BuildQuickPreview(resultArea); Grid.SetRow(body, 2); panel.Children.Add(body);
        _selectedPath = new TextBlock { Foreground = Brush("#6F737A"), FontSize = UiTypography.SecondarySize, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 12, Margin = new(19, 0, 24, 0) };
        footer.Children.Add(_selectedPath);
        Grid.SetRow(footer, 3); panel.Children.Add(footer);
        _status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) { ToolTip.SetTip(_selectedPath, _status.Text); if (_results.ItemCount == 0 && !string.IsNullOrEmpty(_query.Text)) _selectedPath.Text = _status.Text; } };
        var frame = new Border { Child = panel, Background = Brush("#27282C"), BorderThickness = new(1), BorderBrush = Brush("#393B40"), CornerRadius = new(7) };
        TextElement.SetFontSize(frame, UiTypography.Size); TextElement.SetFontFamily(frame, UiTypography.Family); TextElement.SetForeground(frame, Brush("#BCBEC4"));
        Closing += (_, e) => { if (!e.Cancel && !_opening) _closing = true; };
        Deactivated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Finish WM_MOUSEACTIVATE on the owner's embedded WebView before
            // destroying this owned HWND. Closing inside WM_ACTIVATE lets Windows
            // activate the previous application instead of the clicked owner.
            if (IsActive || _closing || _closed || _opening || maskFlyout.IsOpen) return;
            _dismissedOutside = true;
            CloseSearch();
        }, DispatcherPriority.Background);
        maskFlyout.Closed += (_, _) => { if (_closed || _closing) return; if (!IsActive) { _dismissedOutside = true; CloseSearch(); } else _query.Focus(); };
        Closed += (_, _) => maskFlyout.Hide();
        UpdateQuickCategoryVisuals(); return frame;
    }

    private void SelectQuickCategory(QuickCategory category)
    {
        if (_category == category) { _query.Focus(); return; }
        var wasActionSearch = IsActionSearch;
        _query.Focus(); _category = category; Invalidate();
        if (wasActionSearch && category != QuickCategory.Actions && _query.Text?.StartsWith('/') == true) _query.Text = _query.Text[1..];
        _files.IsChecked = category != QuickCategory.Text; _content.IsChecked = category == QuickCategory.Text;
        UpdateQuickCategoryVisuals(); _query.Focus(); _delay.Start();
    }

    private void UpdateQuickCategoryVisuals()
    {
        foreach (var (category, button) in _categoryButtons)
        {
            button.Classes.Set("active", category == _category);
            button.Background = category == _category ? Brush("#23334F") : Brushes.Transparent;
            button.BorderBrush = category == _category ? Brush("#455579") : Brushes.Transparent;
        }
        if (_textScope is not null) _textScope.IsVisible = _category == QuickCategory.Text;
        if (_textScope is not null) _textScope.Margin = _category == QuickCategory.Text ? new Thickness(0, 0, 4, 0) : new Thickness(0);
        if (_fileTypes is not null) _fileTypes.IsVisible = _category == QuickCategory.Text;
        if (_commands is not null) _commands.IsVisible = _category == QuickCategory.All && !IsActionSearch;
        UpdatePreviewLayout();
        _query.PlaceholderText = IsActionSearch ? "Search available actions…" : _category == QuickCategory.Text ? "Search working files…" : _category == QuickCategory.All ? "File name or path; / for actions" : "File name or path; :line to navigate";
    }

    private Control QuickResultRow(CodeSearchMatch match)
    {
        var isText = match.Fingerprint is not null;
        var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("16,*,Auto,Auto"), ColumnSpacing = isText ? 3 : 5, VerticalAlignment = VerticalAlignment.Center };
        Control icon;
        if (isText) icon = new Border { Width = 11, Height = 11, BorderThickness = new(1), BorderBrush = Brush("#BCBEC4"), CornerRadius = new(1), Child = new TextBlock { Text = "T", FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        else if (Path.GetExtension(match.Path).Equals(".md", StringComparison.OrdinalIgnoreCase)) icon = new TextBlock { Text = "M↓", FontWeight = FontWeight.Bold, FontSize = 9, Foreground = Brush("#548AF7"), VerticalAlignment = VerticalAlignment.Center };
        else icon = QuickIcon("M3 2 H9 L13 6 V14 H3 Z M9 2 V6 H13");
        row.Children.Add(icon);
        var text = new TextBlock { LineHeight = 18, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var label = isText ? match.Preview.TrimStart() : Path.GetFileName(match.Path);
        var needle = _query.Text ?? "";
        if (!isText) for (var i = 0; i < 2; i++)
        {
            var colon = needle.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(needle[(colon + 1)..], out var number) || number <= 0) break;
            needle = needle[..colon];
        }
        var at = needle.Length > 0 ? label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) : -1;
        if (at >= 0)
        {
            text.Inlines!.Add(new Run(label[..at]));
            var hit = new Run(label.Substring(at, needle.Length)) { Background = Brush("#BA9955"), Foreground = Brushes.Black };
            if (isText)
            {
                _textHits[match] = hit;
                if (_category == QuickCategory.All && !Equals(_results.SelectedItem, match)) { hit.Background = Brushes.Transparent; hit.Foreground = Brush("#BCBEC4"); }
            }
            text.Inlines.Add(hit);
            text.Inlines.Add(new Run(label[(at + needle.Length)..]));
        }
        else text.Text = label;
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var directory = Path.GetDirectoryName(match.Path)?.Replace('\\', '/');
        var location = new TextBlock { Text = isText ? $"{match.Path} {match.Line}" : string.IsNullOrEmpty(directory) ? Path.GetFileName(_root) : $"{Path.GetFileName(_root)} / {directory}", Classes = { "idea-result-location" }, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(location, 2); row.Children.Add(location);
        if (!isText) { var folder = QuickIcon("M1.5 4 H6 L7.5 5.5 H14 V13 H1.5 Z M8.5 9.5 H15 V15 H8.5 Z"); Grid.SetColumn(folder, 3); row.Children.Add(folder); }
        ToolTip.SetTip(row, $"{match.Path}:{match.Line}:{match.Column}\n{match.Preview}");
        AutomationProperties.SetName(row, $"{match.Path}:{match.Line}:{match.Column} {match.Preview}"); return row;
    }

    private bool HandleQuickNavigation(KeyEventArgs key)
    {
        var choosing = _query.IsKeyboardFocusWithin || _results.IsKeyboardFocusWithin || _categoryButtons.Values.Any(button => button.IsKeyboardFocusWithin);
        if (choosing && key.KeyModifiers == KeyModifiers.None && key.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown)
        {
            var step = key.Key is Key.PageUp or Key.PageDown ? Math.Max(1, (int)(_results.Bounds.Height / 22)) : 1;
            if (key.Key is Key.Up or Key.PageUp) step = -step;
            if (_results.ItemCount > 0) _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + step, 0, _results.ItemCount - 1);
            _query.Focus(); key.Handled = true; return true;
        }
        if (choosing && key.Key == Key.Tab && key.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        { SelectQuickCategory((QuickCategory)(((int)_category + (key.KeyModifiers == KeyModifiers.Shift ? 3 : 1)) % 4)); key.Handled = true; return true; }
        return false;
    }

    private void SetQuickEmpty(string text)
    {
        if (!_quick || _empty is null) return;
        _empty.Text = text; _empty.IsVisible = _results.ItemCount == 0;
    }

    private void UpdateQuickSelection()
    {
        QueuePreview();
        if (_selectedPath is null) return;
        foreach (var (hitMatch, hit) in _textHits)
        {
            var highlighted = _category != QuickCategory.All || Equals(_results.SelectedItem, hitMatch);
            hit.Background = highlighted ? Brush("#BA9955") : Brushes.Transparent;
            hit.Foreground = highlighted ? Brushes.Black : Brush("#BCBEC4");
        }
        _selectedPath.Text = _results.SelectedItem is CodeSearchMatch match ? Path.Combine(_root, match.Path).Replace('\\', '/') : _results.SelectedItem is ShellCommandDefinition command ? $"{command.Group} · {command.Header}" : "";
        ToolTip.SetTip(_selectedPath, _selectedPath.Text);
    }

    private void UpdateQuickFilters() { if (_fileTypes is not null) _fileTypes.Content = FileTypeContent(string.IsNullOrWhiteSpace(_mask.Text) ? "All file types" : _mask.Text!); }

    private static Control FileTypeContent(string caption)
    {
        var content = new Grid { Width = 116, Height = 20, ColumnDefinitions = ColumnDefinitions.Parse("92,24") };
        content.Children.Add(new TextBlock { Text = caption, Margin = new(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var arrow = new Border { BorderThickness = new(1, 0, 0, 0), BorderBrush = Brush("#43454A"), Child = QuickIcon("M4 6 L8 10 L12 6") };
        Grid.SetColumn(arrow, 1); content.Children.Add(arrow); return content;
    }

    private static Button QuickButton(object content, double width)
    {
        var button = new Button { Content = content, Width = width, Height = 24, MinHeight = 0, Padding = new(0), BorderThickness = new(1), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, CornerRadius = new(4), FontSize = UiTypography.Size, FocusAdorner = null };
        button.Template = new FuncControlTemplate<Button>((control, _) => new Border
        {
            CornerRadius = new(4), BorderThickness = control.BorderThickness,
            [!Border.BackgroundProperty] = control[!Button.BackgroundProperty], [!Border.BorderBrushProperty] = control[!Button.BorderBrushProperty],
            Child = new ContentPresenter { Name = "IdeaContent", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, [!ContentPresenter.ContentProperty] = control[!Button.ContentProperty] }
        });
        button.PointerEntered += (_, _) => { if (!button.Classes.Contains("active")) button.Background = Brush("#333438"); };
        button.PointerExited += (_, _) => { if (!button.Classes.Contains("active")) button.Background = Brushes.Transparent; };
        return button;
    }

    private bool IsActionSearch => _quick && (_category == QuickCategory.Actions ||
        _category == QuickCategory.All && _query.Text?.StartsWith('/') == true);

    private static Control QuickActionRow(ShellCommandDefinition command)
    {
        var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(new TextBlock { Text = command.Header, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var gesture = new TextBlock { Text = command.Gesture(OperatingSystem.IsMacOS()) ?? command.Group, Classes = { "idea-result-location" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(gesture, 1); row.Children.Add(gesture);
        AutomationProperties.SetName(row, $"{command.Header} {gesture.Text}");
        return row;
    }

    private static Control QuickIcon(string data) => new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Width = 16, Height = 16, Stroke = Brush("#BCBEC4"), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
