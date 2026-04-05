using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.UI.Application;

internal sealed class AnalysisLabFacade : IAnalysisLabFacade
{
    private readonly IBiometricRepository _repository;
    private readonly IAnalysisBucketStrategy[] _bucketStrategies;

    public AnalysisLabFacade(
        IBiometricRepository repository,
        params IAnalysisBucketStrategy[] bucketStrategies)
    {
        _repository = repository;
        _bucketStrategies = bucketStrategies;
    }

    public async ValueTask<AnalysisSnapshot> AnalyzeAsync(
        BiometricType primaryMetric,
        BiometricType secondaryMetric,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        if (primaryMetric == secondaryMetric)
        {
            throw new InvalidOperationException("Choose two different metrics for comparison.");
        }

        var spec = ResolveBucketSpec(to - from);
        var primaryTask = _repository.GetAggregatesAsync(primaryMetric, from, to, spec.BucketSizeSeconds, ct).AsTask();
        var secondaryTask = _repository.GetAggregatesAsync(secondaryMetric, from, to, spec.BucketSizeSeconds, ct).AsTask();

        await Task.WhenAll(primaryTask, secondaryTask);

        var points = Join(primaryTask.Result, secondaryTask.Result);
        if (points.Count == 0)
        {
            return new AnalysisSnapshot(
                Points: Array.Empty<AnalysisDataPoint>(),
                CorrelationCoefficient: 0d,
                CorrelationLabel: "No overlap",
                BucketLabel: spec.Label,
                InsightHeadline: "No overlapping buckets were found for the selected metrics.",
                PrimaryAverage: 0d,
                SecondaryAverage: 0d);
        }

        double coefficient = ComputePearson(points);
        double primaryAverage = points.Average(p => p.PrimaryValue);
        double secondaryAverage = points.Average(p => p.SecondaryValue);
        string label = DescribeRelationship(coefficient);

        return new AnalysisSnapshot(
            Points: points,
            CorrelationCoefficient: coefficient,
            CorrelationLabel: label,
            BucketLabel: spec.Label,
            InsightHeadline: BuildHeadline(primaryMetric, secondaryMetric, coefficient),
            PrimaryAverage: primaryAverage,
            SecondaryAverage: secondaryAverage);
    }

    private AnalysisBucketSpec ResolveBucketSpec(TimeSpan span)
    {
        foreach (var strategy in _bucketStrategies)
        {
            if (strategy.CanHandle(span))
            {
                return strategy.GetBucketSpec(span);
            }
        }

        throw new InvalidOperationException("No analysis bucket strategy was registered.");
    }

    private static IReadOnlyList<AnalysisDataPoint> Join(
        IReadOnlyList<AggregateBucket> primary,
        IReadOnlyList<AggregateBucket> secondary)
    {
        var secondaryMap = new Dictionary<DateTimeOffset, AggregateBucket>(secondary.Count);
        for (int i = 0; i < secondary.Count; i++)
        {
            secondaryMap[secondary[i].BucketStart] = secondary[i];
        }

        var results = new List<AnalysisDataPoint>(Math.Min(primary.Count, secondary.Count));
        for (int i = 0; i < primary.Count; i++)
        {
            if (!secondaryMap.TryGetValue(primary[i].BucketStart, out var match))
            {
                continue;
            }

            results.Add(new AnalysisDataPoint(
                Timestamp: primary[i].BucketStart,
                PrimaryValue: primary[i].Avg,
                SecondaryValue: match.Avg));
        }

        return results;
    }

    private static double ComputePearson(IReadOnlyList<AnalysisDataPoint> points)
    {
        if (points.Count < 2)
        {
            return 0d;
        }

        double meanX = points.Average(p => p.PrimaryValue);
        double meanY = points.Average(p => p.SecondaryValue);
        double sumXY = 0d;
        double sumXX = 0d;
        double sumYY = 0d;

        for (int i = 0; i < points.Count; i++)
        {
            double x = points[i].PrimaryValue - meanX;
            double y = points[i].SecondaryValue - meanY;
            sumXY += x * y;
            sumXX += x * x;
            sumYY += y * y;
        }

        if (sumXX <= double.Epsilon || sumYY <= double.Epsilon)
        {
            return 0d;
        }

        return Math.Clamp(sumXY / Math.Sqrt(sumXX * sumYY), -1d, 1d);
    }

    private static string DescribeRelationship(double coefficient)
    {
        double abs = Math.Abs(coefficient);
        if (abs >= 0.75d) return coefficient > 0 ? "Strong positive" : "Strong inverse";
        if (abs >= 0.45d) return coefficient > 0 ? "Moderate positive" : "Moderate inverse";
        if (abs >= 0.2d) return coefficient > 0 ? "Weak positive" : "Weak inverse";
        return "Little to no relationship";
    }

    private static string BuildHeadline(
        BiometricType primaryMetric,
        BiometricType secondaryMetric,
        double coefficient)
    {
        if (Math.Abs(coefficient) < 0.2d)
        {
            return $"{primaryMetric} and {secondaryMetric} are mostly decoupled in the selected range.";
        }

        string direction = coefficient > 0 ? "move together" : "move against each other";
        return $"{primaryMetric} and {secondaryMetric} {direction} across the selected window.";
    }
}
