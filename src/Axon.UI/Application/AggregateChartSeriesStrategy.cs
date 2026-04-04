using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.UI.Rendering;
using Axon.UI.ViewModels;

namespace Axon.UI.Application;

internal sealed class AggregateChartSeriesStrategy : IChartSeriesStrategy
{
    private readonly int _threshold;

    public AggregateChartSeriesStrategy(int threshold) => _threshold = threshold;

    public bool CanHandle(TimeSpan span) => true;

    public async ValueTask<ChartSeriesResult> LoadAsync(
        IBiometricRepository repository,
        BiometricType type,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var span = to - from;
        int bucketSizeSeconds = Math.Max(3600, (int)Math.Ceiling(span.TotalSeconds / _threshold));
        var buckets = await repository.GetAggregatesAsync(type, from, to, bucketSizeSeconds, ct);

        var points = new ChartPoint[buckets.Count];
        double min = 0d;
        double max = 100d;

        if (buckets.Count > 0)
        {
            min = buckets[0].Min;
            max = buckets[0].Max;
        }

        for (int i = 0; i < buckets.Count; i++)
        {
            points[i] = new ChartPoint(buckets[i].BucketStart, buckets[i].Avg);
            min = Math.Min(min, buckets[i].Min);
            max = Math.Max(max, buckets[i].Max);
        }

        var reduced = points.Length > _threshold
            ? await LttbDownsampler.DownsampleAsync(points, _threshold, ct)
            : points;

        return ChartSeriesResultFactory.Create(reduced, min, max);
    }
}
