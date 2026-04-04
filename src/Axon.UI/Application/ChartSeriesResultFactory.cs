using Axon.UI.ViewModels;

namespace Axon.UI.Application;

internal static class ChartSeriesResultFactory
{
    public static ChartSeriesResult Create(IReadOnlyList<ChartPoint> points)
    {
        if (points.Count == 0)
        {
            return new ChartSeriesResult(Array.Empty<ChartPoint>(), 0d, 100d);
        }

        double min = points[0].Value;
        double max = points[0].Value;
        for (int i = 1; i < points.Count; i++)
        {
            min = Math.Min(min, points[i].Value);
            max = Math.Max(max, points[i].Value);
        }

        if (Math.Abs(max - min) < 0.001d)
        {
            min -= 1d;
            max += 1d;
        }

        double padding = Math.Max((max - min) * 0.12d, 1d);
        return new ChartSeriesResult(points, min - padding, max + padding);
    }

    public static ChartSeriesResult Create(
        IReadOnlyList<ChartPoint> points,
        double min,
        double max)
    {
        if (Math.Abs(max - min) < 0.001d)
        {
            min -= 1d;
            max += 1d;
        }

        double padding = Math.Max((max - min) * 0.12d, 1d);
        return new ChartSeriesResult(points, min - padding, max + padding);
    }
}
