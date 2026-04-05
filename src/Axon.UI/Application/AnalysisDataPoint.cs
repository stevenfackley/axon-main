namespace Axon.UI.Application;

internal sealed record AnalysisDataPoint(
    DateTimeOffset Timestamp,
    double PrimaryValue,
    double SecondaryValue);
