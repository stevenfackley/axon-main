using Axon.Core.Domain;

namespace Axon.UI.ViewModels;

public sealed record MetricOption(BiometricType Type, string Label)
{
    public override string ToString() => Label;
}
