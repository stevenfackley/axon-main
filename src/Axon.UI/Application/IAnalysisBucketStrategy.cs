namespace Axon.UI.Application;

internal interface IAnalysisBucketStrategy
{
    bool CanHandle(TimeSpan span);

    AnalysisBucketSpec GetBucketSpec(TimeSpan span);
}
