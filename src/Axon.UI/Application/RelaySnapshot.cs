namespace Axon.UI.Application;

internal sealed record RelaySnapshot(
    RelayState State,
    int PendingCount,
    DateTimeOffset? LastSuccessfulSync,
    string? LastError,
    bool AirGapEnabled,
    string TransportName);
