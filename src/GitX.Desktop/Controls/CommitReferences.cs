using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitX.Core;
using GitX.Desktop.Services;

namespace GitX.Desktop.Controls;

public sealed class CommitReferences : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<HistoryReference>?> ReferencesProperty =
        AvaloniaProperty.Register<CommitReferences, IReadOnlyList<HistoryReference>?>(nameof(References));
    public IReadOnlyList<HistoryReference>? References { get => GetValue(ReferencesProperty); set => SetValue(ReferencesProperty, value); }

    public CommitReferences() { Orientation = Orientation.Horizontal; Spacing = 3; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ReferencesProperty) return;
        Children.Clear();
        var references = References ?? [];
        foreach (var reference in references.Take(2))
        {
            var (kind, color) = reference.Kind switch
            {
                "head" => ("", "#B3AEF5"), "local" => ("分支 ", "#79B987"),
                "remote" => ("远程 ", "#78A5DB"), "tag" => ("标签 ", "#D6B571"), _ => ("", "#A0A0A0")
            };
            var badge = new Border
            {
                BorderBrush = Brush.Parse(color), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0), MaxWidth = 65,
                Child = new TextBlock { Text = kind + reference.Name, Foreground = Brush.Parse(color), FontSize = UiTypography.GitLogMetaSize,
                    TextTrimming = TextTrimming.CharacterEllipsis }
            };
            ToolTip.SetTip(badge, reference.CanonicalName);
            Children.Add(badge);
        }
        if (references.Count > 2)
        {
            var more = new TextBlock { Text = $"+{references.Count - 2}", FontSize = UiTypography.GitLogMetaSize, Foreground = Brush.Parse("#A0A0A0"), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(more, string.Join('\n', references.Skip(2).Select(r => r.CanonicalName)));
            Children.Add(more);
        }
    }
}
