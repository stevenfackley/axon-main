using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.UI.Application;

internal interface IChartSeriesStrategy
{
    bool CanHandle(TimeSpan span);

    ValueTask<ChartSeriesResult> LoadAsync(
        IBiometricRepository repository,
        BiometricType type,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);
}
