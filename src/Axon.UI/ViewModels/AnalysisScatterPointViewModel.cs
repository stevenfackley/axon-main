namespace Axon.UI.ViewModels;

public sealed record AnalysisScatterPointViewModel(
    DateTimeOffset Timestamp,
    double X,
    double Y);
