namespace Axon.UI.ViewModels;

public sealed record AnalysisTimeframeOption(string Label, TimeSpan Span)
{
    public override string ToString() => Label;
}
