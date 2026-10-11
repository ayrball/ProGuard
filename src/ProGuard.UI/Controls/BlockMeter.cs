using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ProGuard.UI.Controls;

/// <summary>
/// A bar made of discrete blocks: filled blocks in the text colour, empty ones as faint outlines. Follows
/// the inherited text colour, so it inverts with a selected row.
/// </summary>
public sealed class BlockMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BlockMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(BlockMeter), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(
        nameof(Blocks), typeof(int), typeof(BlockMeter), new FrameworkPropertyMetadata(20, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(BlockMeter), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    static BlockMeter()
    {
        // Re-render when the inherited text colour flips (row selected / deselected).
        TextElement.ForegroundProperty.AddOwner(typeof(BlockMeter),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Blocks { get => (int)GetValue(BlocksProperty); set => SetValue(BlocksProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        int blocks = Math.Max(1, Blocks);
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var ink = (Brush)GetValue(TextElement.ForegroundProperty);
        double fraction = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);
        // Any non-zero value lights at least one block, so "barely busy" still reads as "busy".
        int lit = fraction <= 0 ? 0 : Math.Max(1, (int)Math.Round(fraction * blocks));

        double blockWidth = (w - Gap * (blocks - 1)) / blocks;
        var outline = new Pen(ink, 1) { };
        outline.Freeze();

        for (int i = 0; i < blocks; i++)
        {
            var rect = new Rect(i * (blockWidth + Gap), 0, Math.Max(1, blockWidth), h);
            if (i < lit)
            {
                dc.DrawRectangle(ink, null, rect);
            }
            else
            {
                dc.PushOpacity(0.22);
                dc.DrawRectangle(null, outline, new Rect(rect.X + 0.5, rect.Y + 0.5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1)));
                dc.Pop();
            }
        }
    }
}
