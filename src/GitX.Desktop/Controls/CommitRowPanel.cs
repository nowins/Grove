using Avalonia;
using Avalonia.Controls;

namespace GitX.Desktop.Controls;

public sealed class CommitRowPanel : Grid
{
    public static readonly StyledProperty<bool> ShowAuthorProperty = AvaloniaProperty.Register<CommitRowPanel, bool>(nameof(ShowAuthor), true);
    public static readonly StyledProperty<bool> ShowDateProperty = AvaloniaProperty.Register<CommitRowPanel, bool>(nameof(ShowDate), true);
    public static readonly StyledProperty<bool> ShowReferencesProperty = AvaloniaProperty.Register<CommitRowPanel, bool>(nameof(ShowReferences), true);
    public bool ShowAuthor { get => GetValue(ShowAuthorProperty); set => SetValue(ShowAuthorProperty, value); }
    public bool ShowDate { get => GetValue(ShowDateProperty); set => SetValue(ShowDateProperty, value); }
    public bool ShowReferences { get => GetValue(ShowReferencesProperty); set => SetValue(ShowReferencesProperty, value); }
    public CommitRowPanel() => SizeChanged += (_, _) => UpdateColumns();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowAuthorProperty || change.Property == ShowDateProperty || change.Property == ShowReferencesProperty) UpdateColumns();
    }
    private void UpdateColumns()
    {
        if (ColumnDefinitions.Count < 5) return;
        var compact = Bounds.Width < 600;
        ColumnDefinitions[3].Width = new(!compact && ShowAuthor ? 100 : 0);
        ColumnDefinitions[4].Width = new(!compact && ShowDate ? 126 : 0);
        foreach (var child in Children)
            child.IsVisible = GetColumn(child) switch { 1 => ShowReferences, 3 => !compact && ShowAuthor, 4 => !compact && ShowDate, _ => true };
    }
}
