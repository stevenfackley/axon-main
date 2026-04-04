using Axon.UI.ViewModels;

namespace Axon.UI.Application;

internal sealed record ChartSeriesResult(
    IReadOnlyList<ChartPoint> Points,
    double MinValue,
    double MaxValue);
