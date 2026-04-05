using Axon.Core.Domain;

namespace Axon.UI.Application;

internal interface IAnalysisLabFacade
{
    ValueTask<AnalysisSnapshot> AnalyzeAsync(
        BiometricType primaryMetric,
        BiometricType secondaryMetric,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);
}
