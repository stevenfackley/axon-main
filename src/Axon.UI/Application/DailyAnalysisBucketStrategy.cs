namespace Axon.UI.Application;

internal sealed class DailyAnalysisBucketStrategy : IAnalysisBucketStrategy
{
    private readonly TimeSpan _maxSpan;
    private readonly int _bucketSizeSeconds;
    private readonly string _label;

    public DailyAnalysisBucketStrategy(TimeSpan maxSpan, int bucketSizeSeconds, string label)
    {
        _maxSpan = maxSpan;
        _bucketSizeSeconds = bucketSizeSeconds;
        _label = label;
    }

    public bool CanHandle(TimeSpan span) => span <= _maxSpan;

    public AnalysisBucketSpec GetBucketSpec(TimeSpan span) => new(_bucketSizeSeconds, _label);
}
