using System.Windows;
using System.Windows.Media;

namespace ProGuard.UI.Controls;

/// <summary>A filled line chart of the last N samples, newest on the right, scaled 0..<see cref="Maximum"/>.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values == null || values.Length < 2 || w <= 0 || h <= 0) return;

        // Leading NaNs are "no sample yet": the line starts at the first real sample.
        int first = Array.FindIndex(values, double.IsFinite);
        if (first < 0 || first > values.Length - 2) return;

        double max = Maximum <= 0 ? 1 : Maximum;
        double inset = StrokeThickness / 2;
        double step = w / (values.Length - 1);
        Point At(int i) => new(i * step, inset + (h - 2 * inset) * (1 - Math.Clamp(values[i] / max, 0, 1)));

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(At(first), isFilled: false, isClosed: false);
            for (int i = first + 1; i < values.Length; i++) ctx.LineTo(At(i), isStroked: true, isSmoothJoin: true);
        }
        line.Freeze();

        if (Fill != null)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(first * step, h), isFilled: true, isClosed: true);
                for (int i = first; i < values.Length; i++) ctx.LineTo(At(i), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(w, h), isStroked: false, isSmoothJoin: false);
            }
            area.Freeze();
            dc.DrawGeometry(Fill, null, area);
        }

        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Bevel };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        // The live end of the line gets a solid square, like a cursor.
        var end = At(values.Length - 1);
        double s = StrokeThickness * 3;
        dc.DrawRectangle(Stroke, null, new Rect(end.X - s, end.Y - s / 2, s, s));
    }
}
