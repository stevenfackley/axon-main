namespace Axon.UI.Application;

internal interface IOutboxRelayService : IAsyncDisposable
{
    event EventHandler<RelaySnapshot>? SnapshotChanged;

    RelaySnapshot Current { get; }

    Task StartAsync(CancellationToken ct = default);

    void SetAirGapEnabled(bool enabled);

    Task RefreshAsync(CancellationToken ct = default);
}
