namespace Axon.UI.Application;

internal readonly record struct AnalysisBucketSpec(
    int BucketSizeSeconds,
    string Label);
