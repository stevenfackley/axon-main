using Axon.Core.Ports;
using Axon.Infrastructure.Drivers.Whoop;

namespace Axon.UI.Application;

/// <summary>
/// UI-facing façade over the Whoop integration: connect (interactive OAuth) and
/// sync (ingest new events). Keeps the ViewModel free of infrastructure types and
/// gives the shell a single, simple seam to drive.
/// </summary>
public sealed class WhoopSyncCoordinator
{
    // How far back to pull on the very first sync, before we have a last-sync watermark.
    private static readonly TimeSpan InitialLookback = TimeSpan.FromDays(30);

    private readonly WhoopAuthenticator _authenticator;
    private readonly WhoopDriver _driver;
    private readonly IIngestionOrchestrator _orchestrator;
    private readonly IOAuthTokenStore _tokenStore;

    private DateTimeOffset? _lastSync;

    public WhoopSyncCoordinator(
        WhoopAuthenticator authenticator,
        WhoopDriver driver,
        IIngestionOrchestrator orchestrator,
        IOAuthTokenStore tokenStore,
        bool isConfigured)
    {
        _authenticator = authenticator;
        _driver = driver;
        _orchestrator = orchestrator;
        _tokenStore = tokenStore;
        IsConfigured = isConfigured;
    }

    /// <summary>True when Whoop API credentials are present (client id/secret configured).</summary>
    public bool IsConfigured { get; }

    /// <summary>True when a stored OAuth token exists (the user has connected at least once).</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
        => await _tokenStore.GetTokenAsync(WhoopAuthenticator.DriverId, ct).ConfigureAwait(false) is not null;

    /// <summary>Runs the interactive browser consent flow and persists the resulting token.</summary>
    public Task ConnectAsync(CancellationToken ct = default)
        => _authenticator.AuthenticateInteractiveAsync(ct);

    /// <summary>
    /// Ingests all Whoop events since the last successful sync (or the initial
    /// look-back window on first run), persisting them and triggering inference.
    /// </summary>
    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        var since = _lastSync ?? DateTimeOffset.UtcNow - InitialLookback;
        var incomplete = false;
        void OnFetchIncomplete(object? sender, EventArgs e) => incomplete = true;

        _driver.FetchIncomplete += OnFetchIncomplete;
        try
        {
            await _orchestrator.IngestAsync(_driver, since, ct).ConfigureAwait(false);
        }
        finally
        {
            _driver.FetchIncomplete -= OnFetchIncomplete;
        }

        // Keep the watermark so the next sync fetches the skipped window again.
        if (incomplete)
            throw new InvalidOperationException(
                "Whoop data could not be fetched (not connected, offline, or the Whoop API failed).");

        _lastSync = DateTimeOffset.UtcNow;
    }
}
