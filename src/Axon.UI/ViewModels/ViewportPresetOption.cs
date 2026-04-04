namespace Axon.UI.ViewModels;

public sealed record ViewportPresetOption(string Label, TimeSpan Span)
{
    public override string ToString() => Label;
}
