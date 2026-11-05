using Windows.Foundation;

namespace Brembana;

/// <summary>
/// Mette i figli in fila, ognuno largo quanto serve, e va a capo quando la riga è piena:
/// i chip dei pezzi nel riepilogo della sessione.
/// </summary>
internal sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }

    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

        return Layout(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double maxWidth, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > maxWidth)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            if (arrange)
                child.Arrange(new Rect(x, y, size.Width, size.Height));

            usedWidth = Math.Max(usedWidth, x + size.Width);
            rowHeight = Math.Max(rowHeight, size.Height);
            x += size.Width + HorizontalSpacing;
        }

        return new Size(usedWidth, y + rowHeight);
    }
}
