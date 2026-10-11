using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ProGuard.UI.Controls;

/// <summary>One vertical bar per logical processor, filled from the bottom to its load (0-100).</summary>
public sealed class CoreBars : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(CoreBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(CoreBars), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    static CoreBars()
    {
        TextElement.ForegroundProperty.AddOwner(typeof(CoreBars),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values == null || values.Count == 0 || w <= 0 || h <= 0) return;

        var ink = (Brush)GetValue(TextElement.ForegroundProperty);
        int n = values.Count;
        double gap = Math.Min(Gap, w / n / 2);
        double barWidth = Math.Max(1, (w - gap * (n - 1)) / n);
        var outline = new Pen(ink, 1);
        outline.Freeze();

        for (int i = 0; i < n; i++)
        {
            double x = i * (barWidth + gap);
            double fraction = Math.Clamp(values[i] / 100.0, 0, 1);
            // Any load at all shows at least a sliver, so an idle core reads as "present" rather than "missing".
            double fill = fraction <= 0 ? 0 : Math.Max(2, fraction * h);

            dc.PushOpacity(0.22);
            dc.DrawRectangle(null, outline, new Rect(x + 0.5, 0.5, Math.Max(0, barWidth - 1), Math.Max(0, h - 1)));
            dc.Pop();
            if (fill > 0) dc.DrawRectangle(ink, null, new Rect(x, h - fill, barWidth, fill));
        }
    }
}
