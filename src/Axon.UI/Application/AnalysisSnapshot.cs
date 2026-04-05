namespace Axon.UI.Application;

internal sealed record AnalysisSnapshot(
    IReadOnlyList<AnalysisDataPoint> Points,
    double CorrelationCoefficient,
    string CorrelationLabel,
    string BucketLabel,
    string InsightHeadline,
    double PrimaryAverage,
    double SecondaryAverage);
