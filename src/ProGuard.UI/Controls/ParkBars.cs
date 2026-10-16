using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ProGuard.UI.Controls;

/// <summary>
/// One bar per logical processor: an unparked core is outlined and filled from the bottom to its load (as
/// <see cref="CoreBars"/> draws it); a parked core is a faint block with a short rule across it, so the parked ones
/// read at a glance whatever their load.
/// </summary>
public sealed class ParkBars : FrameworkElement
{
    public static readonly DependencyProperty LoadsProperty = DependencyProperty.Register(
        nameof(Loads), typeof(IReadOnlyList<double>), typeof(ParkBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ParkedProperty = DependencyProperty.Register(
        nameof(Parked), typeof(IReadOnlyList<bool>), typeof(ParkBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ParkBars), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    static ParkBars()
    {
        TextElement.ForegroundProperty.AddOwner(typeof(ParkBars),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public IReadOnlyList<double>? Loads { get => (IReadOnlyList<double>?)GetValue(LoadsProperty); set => SetValue(LoadsProperty, value); }
    public IReadOnlyList<bool>? Parked { get => (IReadOnlyList<bool>?)GetValue(ParkedProperty); set => SetValue(ParkedProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var loads = Loads;
        var parked = Parked;
        int n = Math.Max(loads?.Count ?? 0, parked?.Count ?? 0);
        double w = ActualWidth, h = ActualHeight;
        if (n == 0 || w <= 0 || h <= 0) return;

        var ink = (Brush)GetValue(TextElement.ForegroundProperty);
        double gap = Math.Min(Gap, w / n / 2);
        double barWidth = Math.Max(1, (w - gap * (n - 1)) / n);
        var outline = new Pen(ink, 1.5);
        outline.Freeze();
        var rule = new Pen(ink, 2);
        rule.Freeze();

        for (int i = 0; i < n; i++)
        {
            double x = i * (barWidth + gap);
            var cell = new Rect(x, 0, barWidth, h);
            if (parked != null && i < parked.Count && parked[i])
            {
                dc.PushOpacity(0.12);
                dc.DrawRectangle(ink, null, cell);
                dc.Pop();
                dc.PushOpacity(0.5);
                dc.DrawLine(rule, new Point(x + barWidth * 0.25, h / 2), new Point(x + barWidth * 0.75, h / 2));
                dc.Pop();
                continue;
            }

            double fraction = loads != null && i < loads.Count ? Math.Clamp(loads[i] / 100.0, 0, 1) : 0;
            double fill = Math.Max(3, fraction * h); // an idle unparked core still shows it is awake
            dc.PushOpacity(0.5);
            dc.DrawRectangle(null, outline, new Rect(x + 0.75, 0.75, Math.Max(0, barWidth - 1.5), Math.Max(0, h - 1.5)));
            dc.Pop();
            dc.DrawRectangle(ink, null, new Rect(x, h - fill, barWidth, fill));
        }
    }
}
