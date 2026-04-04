using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.UI.Rendering;
using Axon.UI.ViewModels;

namespace Axon.UI.Application;

internal sealed class RawChartSeriesStrategy : IChartSeriesStrategy
{
    private readonly TimeSpan _maxSpan;
    private readonly int _threshold;

    public RawChartSeriesStrategy(TimeSpan maxSpan, int threshold)
    {
        _maxSpan = maxSpan;
        _threshold = threshold;
    }

    public bool CanHandle(TimeSpan span) => span <= _maxSpan;

    public async ValueTask<ChartSeriesResult> LoadAsync(
        IBiometricRepository repository,
        BiometricType type,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var events = await repository.QueryRangeAsync(type, from, to, ct);
        var points = new ChartPoint[events.Count];
        for (int i = 0; i < events.Count; i++)
        {
            points[i] = new ChartPoint(events[i].Timestamp, events[i].Value);
        }

        var reduced = points.Length > _threshold
            ? await LttbDownsampler.DownsampleAsync(points, _threshold, ct)
            : points;

        return ChartSeriesResultFactory.Create(reduced);
    }
}
