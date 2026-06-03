using System.Windows;
using System.Windows.Media;

namespace DataVortex.App.Controls;

/// <summary>
/// A lightweight, dependency-free line chart with gradient fill support. Bind <see cref="Values"/>
/// to a freshly-assigned <c>double[]</c> each tick; the control auto-scales to the max and redraws.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoGradientFillProperty = DependencyProperty.Register(
        nameof(AutoGradientFill), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool AutoGradientFill
    {
        get => (bool)GetValue(AutoGradientFillProperty);
        set => SetValue(AutoGradientFillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values is null || values.Count < 2 || w <= 0 || h <= 0) return;

        double max = 0;
        foreach (var v in values) if (v > max) max = v;
        if (max <= 0) max = 1;

        const double pad = 2;
        double usableH = Math.Max(1, h - pad * 2);
        double stepX = w / (values.Count - 1);

        bool wantFill = Fill is not null || AutoGradientFill;

        // Build line geometry
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var start = new Point(0, h - pad - values[0] / max * usableH);
            ctx.BeginFigure(start, isFilled: wantFill, isClosed: false);
            var points = new List<Point>(values.Count);
            for (int i = 1; i < values.Count; i++)
                points.Add(new Point(i * stepX, h - pad - values[i] / max * usableH));
            ctx.PolyLineTo(points, isStroked: true, isSmoothJoin: true);

            if (wantFill)
            {
                ctx.LineTo(new Point(w, h), true, false);
                ctx.LineTo(new Point(0, h), true, false);
            }
        }
        geometry.Freeze();

        // Determine fill brush
        Brush? fillBrush = Fill;
        if (AutoGradientFill && fillBrush is null && Stroke is SolidColorBrush solid)
        {
            var c = solid.Color;
            fillBrush = new LinearGradientBrush(
                Color.FromArgb(50, c.R, c.G, c.B),
                Color.FromArgb(0, c.R, c.G, c.B),
                new Point(0, 0), new Point(0, 1));
            fillBrush.Freeze();
        }

        if (fillBrush is not null)
            dc.DrawGeometry(fillBrush, null, geometry);

        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
