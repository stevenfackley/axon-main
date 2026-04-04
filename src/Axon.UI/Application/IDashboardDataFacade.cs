using Axon.Core.Domain;

namespace Axon.UI.Application;

internal interface IDashboardDataFacade
{
    ValueTask<DashboardSnapshot> LoadAsync(
        BiometricType activeMetric,
        DateTimeOffset viewportStart,
        DateTimeOffset viewportEnd,
        CancellationToken ct = default);
}
