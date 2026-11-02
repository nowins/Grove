using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitX.Core;

namespace GitX.Desktop.Controls;

public sealed class CommitGraph : Control
{
    public static readonly StyledProperty<HistoryGraphRow?> RowProperty = AvaloniaProperty.Register<CommitGraph, HistoryGraphRow?>(nameof(Row));
    public HistoryGraphRow? Row { get => GetValue(RowProperty); set => SetValue(RowProperty, value); }
    private static readonly IBrush[] Colors = [Brush.Parse("#5E95F8"), Brush.Parse("#5FB865"), Brush.Parse("#DDAA55"), Brush.Parse("#B58BDB"), Brush.Parse("#D87979")];
    static CommitGraph() { AffectsRender<CommitGraph>(RowProperty); AffectsMeasure<CommitGraph>(RowProperty); }
    protected override Size MeasureOverride(Size availableSize) => new((Row?.Width ?? 2) * 12 + 4, 24);
    public override void Render(DrawingContext context)
    {
        if (Row is not { } row) return;
        static double X(int lane) => 8 + lane * 12;
        var height = Bounds.Height; var middle = height / 2;
        foreach (var edge in row.Edges)
        {
            var pen = new Pen(Colors[edge.Color % Colors.Length], 1.5);
            context.DrawLine(pen, new(X(edge.From), edge.Upper ? 0 : middle), new(X(edge.To), edge.Upper ? middle : height));
        }
        var brush = Colors[row.Color % Colors.Length];
        context.DrawEllipse(row.Merge ? Brush.Parse("#2B2D30") : brush, new Pen(brush, 1.5), new(X(row.Lane), middle), 3, 3);
    }
}
