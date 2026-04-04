using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.UI.Rendering;
using Axon.UI.ViewModels;

namespace Axon.UI.Application;

internal sealed class StreamedChartSeriesStrategy : IChartSeriesStrategy
{
    private readonly TimeSpan _maxSpan;
    private readonly int _threshold;

    public StreamedChartSeriesStrategy(TimeSpan maxSpan, int threshold)
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
        var points = new List<ChartPoint>(4096);
        await foreach (var evt in repository.StreamRangeAsync(type, from, to, ct))
        {
            points.Add(new ChartPoint(evt.Timestamp, evt.Value));
        }

        var reduced = points.Count > _threshold
            ? await LttbDownsampler.DownsampleAsync(points, _threshold, ct)
            : points.ToArray();

        return ChartSeriesResultFactory.Create(reduced);
    }
}
