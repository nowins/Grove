using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;

namespace GitX.Desktop.Services;

// Avalonia 12.1.4's native Windows popups share the active window's IMM32
// context, but InputMethodManager supplies coordinates relative to PopupRoot.
// Rebase only that rectangle; keep the original text client and composition.
// Recheck this workaround when upgrading Avalonia's Windows input implementation.
internal sealed class WindowsPopupIme : IDisposable
{
    private readonly Window _owner;
    private readonly Control _content;
    private readonly Dictionary<TextBox, PopupClient> _clients = [];

    public WindowsPopupIme(Window owner, Control content)
    {
        _owner = owner; _content = content;
        content.AddHandler(InputElement.TextInputMethodClientRequestedEvent, ClientRequested,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void ClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        if (e.Source is not TextBox input || e.Client is not { } original || original is PopupClient ||
            TopLevel.GetTopLevel(input) is not PopupRoot popup || popup.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" }) return;
        if (!_clients.TryGetValue(input, out var client) || !ReferenceEquals(client.Original, original))
        {
            client?.Dispose();
            client = new PopupClient(_owner, popup, input, original);
            _clients[input] = client;
        }
        e.Client = client;
    }

    public void Dispose()
    {
        _content.RemoveHandler(InputElement.TextInputMethodClientRequestedEvent, ClientRequested);
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }

    private sealed class PopupClient : TextInputMethodClient, IDisposable
    {
        private readonly Window _owner;
        private readonly PopupRoot _popup;
        private readonly TextBox _input;
        public TextInputMethodClient Original { get; }

        public PopupClient(Window owner, PopupRoot popup, TextBox input, TextInputMethodClient original)
        {
            _owner = owner; _popup = popup; _input = input; Original = original;
            original.TextViewVisualChanged += ViewChanged;
            original.CursorRectangleChanged += CursorChanged;
            original.SurroundingTextChanged += TextChanged;
            original.SelectionChanged += SelectionChangedForward;
            original.ResetRequested += ResetForward;
            original.InputPaneActivationRequested += InputPaneForward;
            popup.PositionChanged += GeometryChanged; owner.PositionChanged += GeometryChanged;
            popup.ScalingChanged += GeometryChanged; owner.ScalingChanged += GeometryChanged;
        }

        public override Visual TextViewVisual => Original.TextViewVisual;
        public override bool SupportsPreedit => Original.SupportsPreedit;
        public override bool SupportsSurroundingText => Original.SupportsSurroundingText;
        public override string SurroundingText => Original.SurroundingText;
        public override TextSelection Selection { get => Original.Selection; set => Original.Selection = value; }
        public override void SetPreeditText(string? text) => Original.SetPreeditText(text);
        public override void SetPreeditText(string? text, int? cursorPos) => Original.SetPreeditText(text, cursorPos);
        public override void ExecuteContextMenuAction(ContextMenuAction action) => Original.ExecuteContextMenuAction(action);

        public override Rect CursorRectangle
        {
            get
            {
                var cursor = Original.CursorRectangle;
                if (TopLevel.GetTopLevel(_input) != _popup || _input.TransformToVisual(_popup) is not { } transform ||
                    !transform.TryInvert(out var inverse) || _owner.DesktopScaling <= 0) return cursor;
                var inPopup = cursor.TransformToAABB(transform);
                var start = _popup.PointToScreen(inPopup.TopLeft);
                var end = _popup.PointToScreen(inPopup.BottomRight);
                var origin = _owner.PointToScreen(default);
                var scale = _owner.DesktopScaling;
                // IMM32 multiplies its input by the active window's desktop scale.
                // PointToScreen accounts for popup placement, DPI and client origin.
                var inOwner = new Rect((start.X - origin.X) / scale, (start.Y - origin.Y) / scale,
                    (end.X - start.X) / scale, (end.Y - start.Y) / scale);
                return inOwner.TransformToAABB(inverse);
            }
        }

        private void ViewChanged(object? sender, EventArgs e) => RaiseTextViewVisualChanged();
        private void CursorChanged(object? sender, EventArgs e) => RaiseCursorRectangleChanged();
        private void TextChanged(object? sender, EventArgs e) => RaiseSurroundingTextChanged();
        private void SelectionChangedForward(object? sender, EventArgs e) => RaiseSelectionChanged();
        private void ResetForward(object? sender, EventArgs e) => RequestReset();
        private void InputPaneForward(object? sender, EventArgs e) => RaiseInputPaneActivationRequested();
        private void GeometryChanged(object? sender, EventArgs e) => RaiseCursorRectangleChanged();

        public void Dispose()
        {
            Original.TextViewVisualChanged -= ViewChanged;
            Original.CursorRectangleChanged -= CursorChanged;
            Original.SurroundingTextChanged -= TextChanged;
            Original.SelectionChanged -= SelectionChangedForward;
            Original.ResetRequested -= ResetForward;
            Original.InputPaneActivationRequested -= InputPaneForward;
            _popup.PositionChanged -= GeometryChanged; _owner.PositionChanged -= GeometryChanged;
            _popup.ScalingChanged -= GeometryChanged; _owner.ScalingChanged -= GeometryChanged;
        }
    }
}
