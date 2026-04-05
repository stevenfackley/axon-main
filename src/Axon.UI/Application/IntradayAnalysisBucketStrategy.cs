namespace Axon.UI.Application;

internal sealed class IntradayAnalysisBucketStrategy : IAnalysisBucketStrategy
{
    private readonly TimeSpan _maxSpan;

    public IntradayAnalysisBucketStrategy(TimeSpan maxSpan) => _maxSpan = maxSpan;

    public bool CanHandle(TimeSpan span) => span <= _maxSpan;

    public AnalysisBucketSpec GetBucketSpec(TimeSpan span) =>
        new(BucketSizeSeconds: 60 * 60, Label: "1-hour buckets");
}
