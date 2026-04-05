using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Axon.UI.ViewModels;

namespace Axon.UI.Rendering;

public sealed class CorrelationScatterPlot : Control
{
    public static readonly StyledProperty<IReadOnlyList<AnalysisScatterPointViewModel>> PointsProperty =
        AvaloniaProperty.Register<CorrelationScatterPlot, IReadOnlyList<AnalysisScatterPointViewModel>>(
            nameof(Points),
            defaultValue: Array.Empty<AnalysisScatterPointViewModel>());

    public static readonly StyledProperty<string> XAxisLabelProperty =
        AvaloniaProperty.Register<CorrelationScatterPlot, string>(nameof(XAxisLabel), "Primary");

    public static readonly StyledProperty<string> YAxisLabelProperty =
        AvaloniaProperty.Register<CorrelationScatterPlot, string>(nameof(YAxisLabel), "Secondary");

    public IReadOnlyList<AnalysisScatterPointViewModel> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string XAxisLabel
    {
        get => GetValue(XAxisLabelProperty);
        set => SetValue(XAxisLabelProperty, value);
    }

    public string YAxisLabel
    {
        get => GetValue(YAxisLabelProperty);
        set => SetValue(YAxisLabelProperty, value);
    }

    static CorrelationScatterPlot()
    {
        AffectsRender<CorrelationScatterPlot>(PointsProperty, XAxisLabelProperty, YAxisLabelProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#121321")), bounds);

        var frame = new Rect(bounds.X + 32, bounds.Y + 16, Math.Max(0, bounds.Width - 56), Math.Max(0, bounds.Height - 44));
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#2D2D4E")), 1), frame);

        if (Points.Count == 0)
        {
            return;
        }

        double minX = Points.Min(p => p.X);
        double maxX = Points.Max(p => p.X);
        double minY = Points.Min(p => p.Y);
        double maxY = Points.Max(p => p.Y);

        if (Math.Abs(maxX - minX) < 0.001d) { minX -= 1d; maxX += 1d; }
        if (Math.Abs(maxY - minY) < 0.001d) { minY -= 1d; maxY += 1d; }

        foreach (var point in Points)
        {
            double normalizedX = (point.X - minX) / (maxX - minX);
            double normalizedY = (point.Y - minY) / (maxY - minY);
            double x = frame.X + normalizedX * frame.Width;
            double y = frame.Bottom - normalizedY * frame.Height;
            context.DrawEllipse(
                new SolidColorBrush(Color.Parse("#00FFC8"), 0.65),
                null,
                new Point(x, y),
                3,
                3);
        }
    }
}
