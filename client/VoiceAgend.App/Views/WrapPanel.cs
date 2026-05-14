using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace VoiceAgend.App.Views;

/// <summary>
/// Wrap-Layout: legt Kinder mit gleicher, **gestreckter** Breite in Zeilen ab.
/// Pro Zeile passen `floor((avail + HSpacing) / (MinItemWidth + HSpacing))` Karten,
/// jede Karte bekommt dann die maximale Breite, sodass die Zeile komplett ausgefüllt ist.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(10.0, OnInvalidate));

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }
    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(10.0, OnInvalidate));

    /// <summary>Mindestbreite pro Karte. Bestimmt die Spaltenanzahl.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(240.0, OnInvalidate));

    private static void OnInvalidate(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapPanel p) p.InvalidateMeasure();
    }

    private int ColumnsFor(double availableWidth)
    {
        if (Children.Count == 0) return 1;
        if (double.IsInfinity(availableWidth) || availableWidth <= 0) return 1;
        var spacing = HorizontalSpacing;
        var cols = (int)Math.Floor((availableWidth + spacing) / (MinItemWidth + spacing));
        return Math.Max(1, Math.Min(cols, Children.Count));
    }

    private double ItemWidthFor(double availableWidth, int cols)
    {
        var spacing = HorizontalSpacing;
        return (availableWidth - spacing * (cols - 1)) / cols;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cols = ColumnsFor(availableSize.Width);
        var itemWidth = double.IsInfinity(availableSize.Width)
            ? MinItemWidth
            : ItemWidthFor(availableSize.Width, cols);

        double rowHeight = 0;
        int colIndex = 0;
        int rows = 0;
        double totalHeight = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            colIndex++;
            if (colIndex >= cols)
            {
                totalHeight += rowHeight;
                rows++;
                if (rows > 0) totalHeight += 0; // spacing wird unten addiert
                rowHeight = 0;
                colIndex = 0;
            }
        }
        if (colIndex > 0) { totalHeight += rowHeight; rows++; }
        if (rows > 1) totalHeight += VerticalSpacing * (rows - 1);

        return new Size(
            double.IsInfinity(availableSize.Width) ? itemWidth * cols + HorizontalSpacing * (cols - 1) : availableSize.Width,
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = ColumnsFor(finalSize.Width);
        var itemWidth = ItemWidthFor(finalSize.Width, cols);

        double x = 0, y = 0, rowHeight = 0;
        int colIndex = 0;

        foreach (var child in Children)
        {
            // Höhe = was Measure ergeben hat (oder neu messen mit fixierter Breite)
            var h = child.DesiredSize.Height;
            child.Arrange(new Rect(x, y, itemWidth, h));
            rowHeight = Math.Max(rowHeight, h);
            colIndex++;
            if (colIndex >= cols)
            {
                y += rowHeight + VerticalSpacing;
                rowHeight = 0; colIndex = 0; x = 0;
            }
            else
            {
                x += itemWidth + HorizontalSpacing;
            }
        }
        return finalSize;
    }
}
