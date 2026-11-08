using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitX.Desktop.Services;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop.Controls;

internal sealed class ToolDockWorkspace : Border, IDisposable
{
    private sealed class Tool(ToolId id, string title, string icon, Control content)
    {
        public ToolId Id { get; } = id;
        public string Title { get; set; } = title;
        public string Icon { get; } = icon;
        public Control Content { get; } = content;
        public bool Available { get; set; } = id != ToolId.FileHistory;
        public Window? Window;
        public bool Independent;
    }
    private sealed class Region
    {
        public Border Frame { get; } = new();
        public TextBlock Title { get; } = new() { Classes = { "tool-title" }, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0) };
        public Grid Contents { get; } = new();
        public ToolId? Active;
    }
    private sealed record Drag(ToolId Id, Control Handle, IPointer Pointer, PixelPoint Start, Window Owner, bool Toggle)
    { public bool Moved; public PixelPoint Last = Start; public bool WasFocusable; public Control? PreviousFocus; }
    private readonly Window _owner;
    private readonly ToolLayoutStore _store;
    private readonly ToolDockLayout _layout = new();
    private readonly Dictionary<ToolId, Tool> _tools = [];
    private readonly Dictionary<ToolEdge, Region> _regions = [];
    private readonly Dictionary<ToolEdge, StackPanel> _bars = [];
    private readonly Grid _site = new() { RowDefinitions = new("0,0,*,0,0"), ColumnDefinitions = new("0,0,*,0,0") };
    private readonly Grid _outer = new() { RowDefinitions = new("0,*,0"), ColumnDefinitions = new("34,*,0") };
    private readonly double[] _sizes = [280, 300, 260, 340];
    private readonly Dictionary<ToolEdge, double> _applied = [];
    private readonly List<(GridSplitter Splitter, ToolEdge Edge)> _splitters = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Window? _preview;
    private Drag? _drag;
    private long _moveGeneration;
    private bool _disposed, _reconciling, _ownerClosing;
    private Task _pendingSave = Task.CompletedTask;
    public event Action<ToolId>? Activated;
    public event Action<Window>? FloatingCreated;
    public event Action<string>? Error;
    public bool EditorFocused { get; private set; }
    public void ToggleEditorFocus() { EditorFocused = !EditorFocused; ResizeRegions(); }
    public Func<ToolId?, Task>? BeforeReparentAsync { get; set; }
    public Func<ToolId?, Task>? AfterReparentAsync { get; set; }
    public Window? ActiveWindow => _tools.Values.Select(t => t.Window).FirstOrDefault(w => w?.IsActive == true);
    public ToolDockWorkspace(Window owner, Control editor, string layoutPath)
    {
        _owner = owner; _store = new(layoutPath); Child = _outer;
        Detach(editor); _site.Children.Add(editor); Grid.SetColumn(editor, 2); Grid.SetRow(editor, 2);
        _outer.Children.Add(_site); Grid.SetColumn(_site, 1); Grid.SetRow(_site, 1);
        var leftRail = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Background = Brush("PanelBrush") };
        _outer.Children.Add(leftRail); Grid.SetRow(leftRail, 1); Grid.SetColumn(leftRail, 0);
        foreach (var edge in Enum.GetValues<ToolEdge>())
        {
            var region = _regions[edge] = CreateRegion(edge);
            var (row, column) = edge switch { ToolEdge.Left => (2, 0), ToolEdge.Right => (2, 4), ToolEdge.Top => (0, 0), _ => (4, 0) };
            _site.Children.Add(region.Frame); Grid.SetRow(region.Frame, row); Grid.SetColumn(region.Frame, column);
            if (edge is ToolEdge.Top or ToolEdge.Bottom) Grid.SetColumnSpan(region.Frame, 5);
            var splitter = new GridSplitter { Background = Brush("BorderBrush"), ResizeDirection = edge is ToolEdge.Left or ToolEdge.Right ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                ResizeBehavior = edge is ToolEdge.Left or ToolEdge.Top ? GridResizeBehavior.PreviousAndNext : GridResizeBehavior.PreviousAndNext };
            _site.Children.Add(splitter); _splitters.Add((splitter, edge));
            Grid.SetRow(splitter, edge switch { ToolEdge.Top => 1, ToolEdge.Bottom => 3, _ => 2 });
            Grid.SetColumn(splitter, edge switch { ToolEdge.Left => 1, ToolEdge.Right => 3, _ => 0 });
            if (edge is ToolEdge.Top or ToolEdge.Bottom) Grid.SetColumnSpan(splitter, 5);
            splitter.PointerReleased += (_, _) => { CaptureSizes(); ScheduleSave(); };
            var bar = _bars[edge] = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new(2) };
            var frame = new Border { Child = bar, Background = Brush("PanelBrush"), BorderBrush = Brush("SubtleBorderBrush"),
                BorderThickness = edge == ToolEdge.Right ? new(1, 0, 0, 0) : new(0, 0, 1, 0) };
            if (edge == ToolEdge.Right)
            { _outer.Children.Add(frame); Grid.SetRow(frame, 1); Grid.SetColumn(frame, 2); }
            else
            { leftRail.Children.Add(frame); Grid.SetRow(frame, edge switch { ToolEdge.Top => 0, ToolEdge.Left => 1, _ => 3 }); }
        }
        SizeChanged += (_, _) => ResizeRegions();
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await PersistAsync(); };
    }
    private static IBrush Brush(string name) => (IBrush)Application.Current!.FindResource(name)!;
    private Region CreateRegion(ToolEdge edge)
    {
        var region = new Region();
        var grid = new Grid { RowDefinitions = new("28,*") };
        var header = new Border { Background = Brush("PanelBrush"), BorderBrush = Brush("SubtleBorderBrush"), BorderThickness = new(0, 0, 0, 1) };
        var headerLayout = new DockPanel(); header.Child = headerLayout;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0) };
        DockPanel.SetDock(actions, Dock.Right); headerLayout.Children.Add(actions);
        Button Action(string icon, string label, Action<Button> action)
        {
            var button = new Button { Classes = { "toolbar", "icon-button", "tool-header-action" }, Content = IconContent.Icon(icon), Focusable = false };
            AutomationProperties.SetName(button, label); ToolTip.SetTip(button, label);
            button.Click += (_, _) => action(button); actions.Children.Add(button); return button;
        }
        Action("MoreIcon", "工具窗口选项", button => { if (region.Active is { } id) ShowMenu(id, button, false); });
        Action("MinusIcon", "隐藏工具窗口 · Shift+Esc", _ => { if (region.Active is { } id) Hide(id); });
        headerLayout.Children.Add(region.Title); grid.Children.Add(header); grid.Children.Add(region.Contents); Grid.SetRow(region.Contents, 1);
        region.Frame.Child = grid; region.Frame.Background = Brush("PanelBrush");
        AttachDrag(header, () => region.Active, false);
        header.ContextRequested += (_, e) =>
        {
            var tab = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault()?.Tag;
            var id = tab is ToolId tool ? tool : region.Active;
            if (id is { } selected) { e.Handled = true; ShowMenu(selected, header, true); }
        };
        return region;
    }
    public void Register(ToolId id, string title, string icon, Control content)
    {
        Detach(content); _tools.Add(id, new(id, title, icon, content)); Reconcile();
    }
    public void SetAvailable(ToolId id, bool available, string? title = null)
    {
        var tool = _tools[id]; tool.Available = available; if (title is not null) tool.Title = title;
        if (!available) _layout.Hide(id); Reconcile();
    }
    public bool Visible(ToolId id) => _tools.TryGetValue(id, out var tool) && tool.Available && _layout[id].Visible;
    public Window WindowFor(ToolId id) => _tools.GetValueOrDefault(id)?.Window ?? _owner;
    public async Task LoadAsync()
    {
        var snapshot = await _store.ReadAsync(); if (_disposed || snapshot is null) return;
        _layout.Restore(snapshot);
        var sizes = new[] { snapshot.Left, snapshot.Right, snapshot.Top, snapshot.Bottom };
        for (var i = 0; i < sizes.Length; i++) if (double.IsFinite(sizes[i]) && sizes[i] is >= 140 and <= 3000) _sizes[i] = sizes[i];
        Reconcile(false);
    }
    public void Show(ToolId id, bool toggle = false, bool focus = true)
    {
        if (_disposed || !_tools.TryGetValue(id, out var tool) || !tool.Available) return;
        var wasFocused = EditorFocused; EditorFocused = false;
        if (toggle && Visible(id) && !wasFocused) { Hide(id); return; }
        _layout.Show(id); Reconcile(focus); ScheduleSave(); if (focus) Focus(id);
    }
    public void Hide(ToolId id)
    { if (_disposed) return; _layout.Hide(id); Reconcile(false); ScheduleSave(); }
    public ToolId? FocusedTool()
    {
        foreach (var tool in _tools.Values)
            if (Visible(tool.Id) && WindowFor(tool.Id).IsActive && (tool.Content.IsKeyboardFocusWithin || tool.Window is { IsActive: true })) return tool.Id;
        return null;
    }
    public void HideFocused()
    { if (FocusedTool() is { } id) Hide(id); }
    public async Task MoveAsync(ToolId id, ToolEdge? edge, PixelPoint? position = null, int? before = null, bool? independent = null)
    {
        if (_disposed || !_tools.TryGetValue(id, out var tool) || !tool.Available) return;
        var generation = ++_moveGeneration;
        try
        {
            if (BeforeReparentAsync is { } prepare) await prepare(id);
            if (_disposed || generation != _moveGeneration) return;
            var active = _owner.IsActive || _tools.Values.Any(t => t.Window?.IsActive == true);
            if (edge is { } side) _layout.Dock(id, side, before);
            else _layout.Float(id, position?.X, position?.Y, independent ?? _layout[id].Independent);
            Reconcile(active);
            if (AfterReparentAsync is { } restore) await restore(id);
            if (_disposed || generation != _moveGeneration) return;
            if (active && (_owner.IsActive || _tools.Values.Any(t => t.Window?.IsActive == true))) Focus(id);
            await PersistAsync();
        }
        catch (Exception e) { if (!_disposed) Error?.Invoke("无法移动工具窗口：" + e.Message); }
    }
    private void Focus(ToolId id)
    {
        var window = WindowFor(id); if (!window.IsActive) window.Activate();
        Dispatcher.UIThread.Post(() => { if (!_disposed && Visible(id) && WindowFor(id).IsActive) Activated?.Invoke(id); }, DispatcherPriority.Loaded);
    }
    private void Reconcile(bool activate = false)
    {
        if (_disposed || _reconciling) return;
        _reconciling = true;
        try
        {
            CaptureSizes();
            foreach (var tool in _tools.Values)
            {
                var state = _layout[tool.Id];
                if (state.Floating && tool.Available)
                {
                    if (tool.Window is { } previous && tool.Independent != state.Independent)
                    { Detach(tool.Content); tool.Window = null; previous.Content = null; previous.Close(); }
                    if (tool.Window is null) CreateFloating(tool, state, activate && state.Visible);
                    tool.Content.IsVisible = true;
                    if (state.Visible && !tool.Window!.IsVisible)
                    { tool.Window.ShowActivated = activate; if (state.Independent) tool.Window.Show(); else tool.Window.Show(_owner); }
                    else if (!state.Visible && tool.Window!.IsVisible) tool.Window.Hide();
                }
                else
                {
                    var host = _regions[state.Edge].Contents;
                    if (tool.Content.Parent != host) { Detach(tool.Content); host.Children.Add(tool.Content); }
                    tool.Content.IsVisible = tool.Available && state.Visible;
                    if (tool.Window is { } floating) { tool.Window = null; floating.Content = null; floating.Close(); }
                }
            }
            foreach (var edge in Enum.GetValues<ToolEdge>())
            {
                var assigned = _layout.Tools.Where(t => t.Edge == edge && _tools.TryGetValue(t.Id, out var item) && item.Available).ToArray();
                var active = assigned.FirstOrDefault(t => t.Visible && !t.Floating);
                var region = _regions[edge]; region.Active = active?.Id;
                region.Title.Text = active is null ? "" : _tools[active.Id].Title;
                ToolTip.SetTip(region.Title, region.Title.Text); _bars[edge].Children.Clear();
                foreach (var state in assigned)
                {
                    var tool = _tools[state.Id];
                    var icon = new Button { Height = 28, MinHeight = 28, Classes = { "toolbar", "tool-stripe" }, Content = IconContent.Icon(tool.Icon), Tag = tool.Id };
                    icon.Width = 28;
                    ToolTip.SetTip(icon, tool.Title + (tool.Id switch { ToolId.Project => " · Ctrl+1", ToolId.Commit => " · Ctrl+2", ToolId.GitLog => " · Ctrl+3", _ => "" }));
                    AutomationProperties.SetName(icon, tool.Title + "工具窗口");
                    icon.Classes.Set("active", state.Visible);
                    icon.Click += (_, _) => Show(tool.Id, true); AttachDrag(icon, () => tool.Id, true);
                    icon.ContextRequested += (_, e) => { e.Handled = true; ShowMenu(tool.Id, icon, true); }; _bars[edge].Children.Add(icon);
                }
                region.Frame.IsVisible = active is not null;
                _bars[edge].IsVisible = assigned.Length > 0;
            }
            _outer.ColumnDefinitions[0].Width = new(new[] { ToolEdge.Left, ToolEdge.Top, ToolEdge.Bottom }.Any(edge => _bars[edge].Children.Count > 0) ? 34 : 0);
            _outer.ColumnDefinitions[2].Width = new(_bars[ToolEdge.Right].Children.Count > 0 ? 34 : 0);
            _outer.RowDefinitions[0].Height = new(0);
            _outer.RowDefinitions[2].Height = new(0);
            ResizeRegions();
        }
        finally { _reconciling = false; }
    }
    private void CreateFloating(Tool tool, ToolPlacement state, bool activate)
    {
        tool.Independent = state.Independent;
        var screen = _owner.Screens.ScreenFromPoint(new((int)state.X, (int)state.Y)) ?? _owner.Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            state = ToolDockLayout.FitFloat(state, area.X, area.Y, area.Width, area.Height, screen.Scaling);
            _layout.SetBounds(tool.Id, state.X, state.Y, state.Width, state.Height);
        }
        var window = tool.Window = new Window { Title = tool.Title + " — Grove", Width = state.Width, Height = state.Height, MinWidth = 280, MinHeight = 200,
            ShowInTaskbar = state.Independent, ShowActivated = activate, WindowStartupLocation = WindowStartupLocation.Manual,
            ExtendClientAreaToDecorationsHint = true, ExtendClientAreaTitleBarHeightHint = 32, WindowDecorationsTheme = _owner.WindowDecorationsTheme };
        WindowsWindowCommands.Attach(window);
        var grid = new Grid { RowDefinitions = new("32,28,*") };
        // Native caption moves/resizes the floating frame; the inner header also docks it.
        var header = new DockPanel { Background = Brush("PanelBrush") };
        var menu = new Button { Classes = { "toolbar", "icon-button", "tool-header-action" }, Content = IconContent.Icon("MoreIcon"), Focusable = false, Margin = new(0, 0, 4, 0) };
        AutomationProperties.SetName(menu, "工具窗口选项"); ToolTip.SetTip(menu, "工具窗口选项");
        DockPanel.SetDock(menu, Dock.Right); header.Children.Add(menu); menu.Click += (_, _) => ShowMenu(tool.Id, menu, false);
        header.Children.Add(new TextBlock { Classes = { "tool-title" }, Text = tool.Title, Margin = new(10, 0), VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(header); Grid.SetRow(header, 1); AttachDrag(header, () => tool.Id, false);
        header.ContextRequested += (_, e) => { e.Handled = true; ShowMenu(tool.Id, header, true); };
        Detach(tool.Content); grid.Children.Add(tool.Content); Grid.SetRow(tool.Content, 2); window.Content = grid;
        window.Position = new((int)state.X, (int)state.Y);
        window.Closing += (_, e) =>
        {
            if (_disposed || _ownerClosing || tool.Window != window) return;
            // Avalonia asks owned children before the owner's Closing event.
            // Only a direct tool close hides it; owner/system close must reach
            // the main window's write/draft guards and layout flush.
            if (e.CloseReason != WindowCloseReason.WindowClosing) return;
            e.Cancel = true; Hide(tool.Id);
        };
        void Capture()
        {
            if (_disposed || !window.IsVisible || tool.Window != window) return;
            _layout.SetBounds(tool.Id, window.Position.X, window.Position.Y, window.Width, window.Height); ScheduleSave();
        }
        window.PositionChanged += (_, _) => Capture(); window.SizeChanged += (_, _) => Capture();
        window.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _drag is not null) { e.Handled = true; CancelDrag(); }
            else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.Shift) { e.Handled = true; Hide(tool.Id); }
        }, RoutingStrategies.Tunnel);
        FloatingCreated?.Invoke(window);
    }
    private void ResizeRegions()
    {
        // Clamp only the rendered sizes. Keep the user's saved sizes so growing
        // the window restores them, rather than persisting a compact constraint.
        var horizontalBudget = Math.Max(0, _site.Bounds.Width - (_site.Bounds.Width < 1100 ? Math.Min(640, _site.Bounds.Width * .74) : 320));
        var verticalBudget = Math.Max(0, _site.Bounds.Height - (_site.Bounds.Height < 700 ? Math.Min(320, _site.Bounds.Height * .60) : 180));
        var wantedW = Enum.GetValues<ToolEdge>().Where(e => e is ToolEdge.Left or ToolEdge.Right && _regions[e].Active is not null).Sum(e => _sizes[(int)e]);
        var wantedH = Enum.GetValues<ToolEdge>().Where(e => e is ToolEdge.Top or ToolEdge.Bottom && _regions[e].Active is not null).Sum(e => _sizes[(int)e]);
        foreach (var edge in Enum.GetValues<ToolEdge>())
        {
            var visible = !EditorFocused && _regions[edge].Active is not null;
            _regions[edge].Frame.IsVisible = visible;
            var amount = visible ? _sizes[(int)edge] : 0;
            amount *= edge is ToolEdge.Left or ToolEdge.Right ? Math.Min(1, horizontalBudget / Math.Max(1, wantedW)) : Math.Min(1, verticalBudget / Math.Max(1, wantedH));
            if (!double.IsFinite(amount)) amount = 0;
            if (edge is ToolEdge.Left or ToolEdge.Right) _site.ColumnDefinitions[edge == ToolEdge.Left ? 0 : 4].Width = new(amount);
            else _site.RowDefinitions[edge == ToolEdge.Top ? 0 : 4].Height = new(amount);
            _applied[edge] = amount;
        }
        foreach (var (splitter, edge) in _splitters)
        {
            var shown = !EditorFocused && _regions[edge].Active is not null; splitter.IsVisible = shown;
            if (edge is ToolEdge.Left or ToolEdge.Right) _site.ColumnDefinitions[edge == ToolEdge.Left ? 1 : 3].Width = new(shown ? 4 : 0);
            else _site.RowDefinitions[edge == ToolEdge.Top ? 1 : 3].Height = new(shown ? 4 : 0);
        }
    }
    private void CaptureSizes()
    {
        foreach (var edge in Enum.GetValues<ToolEdge>())
        {
            if (_regions[edge].Active is null) continue;
            var amount = edge is ToolEdge.Left or ToolEdge.Right ? _site.ColumnDefinitions[edge == ToolEdge.Left ? 0 : 4].Width.Value : _site.RowDefinitions[edge == ToolEdge.Top ? 0 : 4].Height.Value;
            if (amount >= 140 && _applied.TryGetValue(edge, out var applied) && Math.Abs(amount - applied) > .5) _sizes[(int)edge] = amount;
        }
    }
    private void ShowMenu(ToolId id, Control anchor, bool pointer)
    {
        var state = _layout[id]; var flyout = new MenuFlyout();
        MenuItem Item(string label, Action action, bool selected = false)
        { var item = new MenuItem { Header = label, Icon = selected ? IconContent.Icon("CheckIcon") : null }; item.Click += (_, _) => { flyout.Hide(); action(); }; return item; }
        var mode = new MenuItem { Header = "视图模式" };
        mode.Items.Add(Item("停靠固定", () => _ = MoveAsync(id, state.Edge), !state.Floating));
        mode.Items.Add(Item("浮动", () => _ = MoveAsync(id, null, independent: false), state.Floating && !state.Independent));
        mode.Items.Add(Item("窗口", () => _ = MoveAsync(id, null, independent: true), state.Floating && state.Independent)); flyout.Items.Add(mode);
        var move = new MenuItem { Header = "移动到" };
        foreach (var edge in Enum.GetValues<ToolEdge>()) { var side = edge; move.Items.Add(Item(EdgeLabel(edge), () => _ = MoveAsync(id, side), !state.Floating && state.Edge == edge)); }
        flyout.Items.Add(move); flyout.Items.Add(new Separator()); flyout.Items.Add(Item("隐藏", () => Hide(id)));
        flyout.Items.Add(Item("恢复默认布局", () => _ = ResetAsync())); flyout.ShowAt(anchor, pointer);
    }
    public async Task ResetAsync()
    {
        var generation = ++_moveGeneration;
        if (BeforeReparentAsync is { } prepare) await prepare(null); if (_disposed || generation != _moveGeneration) return;
        var active = _owner.IsActive || _tools.Values.Any(t => t.Window?.IsActive == true);
        EditorFocused = false;
        _layout.Reset(); Array.Copy(new double[] { 280, 300, 260, 340 }, _sizes, 4); Reconcile();
        if (active) _owner.Activate();
        if (AfterReparentAsync is { } restore) await restore(null); await PersistAsync();
    }
    private static string EdgeLabel(ToolEdge edge) => edge switch { ToolEdge.Left => "左侧", ToolEdge.Right => "右侧", ToolEdge.Top => "顶部", _ => "底部" };
    private void AttachDrag(Control handle, Func<ToolId?> selected, bool toggle)
    {
        handle.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || _drag is not null) return;
            var button = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault();
            if (button is not null && button.Tag is not ToolId) return;
            var id = button?.Tag is ToolId tab ? tab : selected();
            if (id is null || TopLevel.GetTopLevel(handle) is not Window owner) return;
            var screen = handle.PointToScreen(e.GetPosition(handle));
            _drag = new(id.Value, handle, e.Pointer, screen, owner, toggle) { WasFocusable = handle.Focusable, PreviousFocus = owner.FocusManager?.GetFocusedElement() as Control };
            e.Handled = true; e.Pointer.Capture(handle);
        }, RoutingStrategies.Tunnel);
        handle.PointerMoved += (_, e) =>
        {
            if (_drag is not { } drag || drag.Handle != handle) return;
            var screen = handle.PointToScreen(e.GetPosition(handle)); drag.Last = screen;
            if (!drag.Moved && Math.Abs(screen.X - drag.Start.X) + Math.Abs(screen.Y - drag.Start.Y) < 8) return;
            if (!drag.Moved) { drag.Moved = true; handle.Focusable = true; NativeKeyboardFocus.TryFocusControl(drag.Owner, handle); }
            ShowPreview(drag);
        };
        handle.PointerReleased += (_, e) =>
        {
            if (_drag is not { } drag || drag.Handle != handle) return;
            var screen = handle.PointToScreen(e.GetPosition(handle)); var edge = Target(screen);
            _drag = null; e.Pointer.Capture(null); handle.Focusable = drag.WasFocusable; _preview?.Hide(); e.Handled = true;
            if (!drag.Moved) { Show(drag.Id, drag.Toggle); return; }
            int? before = null;
            if (edge is { } side)
            {
                var bar = _bars[side];
                if (TopLevel.GetTopLevel(bar) is not null)
                {
                    var local = bar.PointToClient(screen);
                    if (local.X >= 0 && local.X <= bar.Bounds.Width && local.Y >= 0 && local.Y <= bar.Bounds.Height)
                        before = bar.Children.TakeWhile(c => c.Bounds.Bottom < local.Y).Count(c => c.Tag is ToolId id && id != drag.Id);
                }
            }
            _ = MoveAsync(drag.Id, edge, edge is null ? new(screen.X - 60, screen.Y - 40) : null, before);
        };
        handle.PointerCaptureLost += (_, _) => { if (_drag?.Handle == handle) CancelDrag(); };
        handle.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && _drag is not null) { e.Handled = true; CancelDrag(); } }, RoutingStrategies.Tunnel);
    }
    public bool CancelDrag()
    {
        if (_drag is not { } drag) return false;
        _drag = null; drag.Pointer.Capture(null); drag.Handle.Focusable = drag.WasFocusable; _preview?.Hide();
        if (!_disposed && drag.Owner.IsActive && drag.PreviousFocus is { IsVisible: true } previous && TopLevel.GetTopLevel(previous) == drag.Owner)
        { if (previous is NativeWebView) previous.Focus(); else NativeKeyboardFocus.TryFocusControl(drag.Owner, previous); }
        return true;
    }
    private ToolEdge? Target(PixelPoint screen)
    {
        foreach (var (edge, bar) in _bars)
        {
            if (!bar.IsVisible || TopLevel.GetTopLevel(bar) is null) continue;
            var point = bar.PointToClient(screen);
            if (point.X >= 0 && point.Y >= 0 && point.X <= bar.Bounds.Width && point.Y <= bar.Bounds.Height) return edge;
        }
        // Existing headers take priority over the outer edge hit zones, so
        // reordering a side tab cannot accidentally move it to the top edge.
        foreach (var (edge, region) in _regions)
        {
            if (!region.Frame.IsVisible || TopLevel.GetTopLevel(region.Frame) is null) continue;
            var header = region.Frame.PointToClient(screen);
            if (header.X >= 0 && header.X <= region.Frame.Bounds.Width && header.Y is >= 0 and <= 28) return edge;
        }
        var local = _site.PointToClient(screen);
        return ToolDockLayout.DropEdge(local.X, local.Y, _site.Bounds.Width, _site.Bounds.Height);
    }
    private void ShowPreview(Drag drag)
    {
        if (_preview is null) _preview = new Window { WindowDecorations = WindowDecorations.None, CanResize = false, ShowInTaskbar = false, ShowActivated = false,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent], Background = Brushes.Transparent, IsHitTestVisible = false };
        var edge = Target(drag.Last); var width = _site.Bounds.Width; var height = _site.Bounds.Height;
        var rect = edge switch
        {
            ToolEdge.Left => new Rect(0, 0, Math.Min(_sizes[0], width * .45), height),
            ToolEdge.Right => new Rect(width - Math.Min(_sizes[1], width * .45), 0, Math.Min(_sizes[1], width * .45), height),
            ToolEdge.Top => new Rect(0, 0, width, Math.Min(_sizes[2], height * .45)),
            ToolEdge.Bottom => new Rect(0, height - Math.Min(_sizes[3], height * .45), width, Math.Min(_sizes[3], height * .45)),
            _ => new Rect(0, 0, 300, 160)
        };
        var point = edge is null ? new PixelPoint(drag.Last.X + 12, drag.Last.Y + 12) : _site.PointToScreen(rect.TopLeft);
        _preview.Position = point; _preview.Width = Math.Max(1, rect.Width); _preview.Height = Math.Max(1, rect.Height);
        _preview.Content = new Border { Background = new SolidColorBrush(Color.Parse("#383574F0")), BorderBrush = Brush("AccentBrush"), BorderThickness = new(2),
            Child = new TextBlock { Text = _tools[drag.Id].Title + " · " + (edge is { } side ? "停靠到" + EdgeLabel(side) : "浮动"), Foreground = Brush("TextBrush"), Margin = new(12) } };
        if (!_preview.IsVisible) _preview.Show(_owner);
    }
    private void ScheduleSave() { if (_disposed) return; _saveTimer.Stop(); _saveTimer.Start(); }
    public async Task PersistAsync()
    {
        if (_disposed) return; CaptureSizes(); var snapshot = new ToolLayoutSnapshot(1, _layout.Tools.ToArray(), _sizes[0], _sizes[1], _sizes[2], _sizes[3]);
        try { _pendingSave = _store.WriteAsync(snapshot); await _pendingSave; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { if (!_disposed) Error?.Invoke("无法保存工具窗口布局：" + e.Message); }
    }
    public async Task FlushAsync()
    {
        await PersistAsync();
        Task saving;
        do { saving = _pendingSave; try { await saving; } catch (IOException) { } catch (UnauthorizedAccessException) { } } while (saving != _pendingSave);
    }
    public void PrepareOwnerClose() => _ownerClosing = true;
    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel: panel.Children.Remove(control); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl host: host.Content = null; break;
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; ++_moveGeneration; CancelDrag(); _saveTimer.Stop(); _preview?.Close();
        foreach (var tool in _tools.Values) { if (tool.Window is { } window) { tool.Window = null; window.Close(); } }
    }
}
