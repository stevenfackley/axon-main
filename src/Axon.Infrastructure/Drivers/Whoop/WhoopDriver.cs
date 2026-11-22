using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Microsoft.Extensions.Logging;

namespace Axon.Infrastructure.Drivers.Whoop;

/// <summary>
/// <see cref="IBiometricDriver"/> implementation for the Whoop 4.0 band.
///
/// Data access modes
/// ─────────────────
/// 1. <b>API mode</b> — fetches data from the Whoop Developer API v1 using an
///    OAuth2 Bearer token stored in <see cref="IOAuthTokenStore"/>.
///    Requires an active internet connection.
///    OAuth2 credentials (client_id / client_secret) are injected via
///    <see cref="WhoopDriverOptions"/>; they are intentionally dummied out
///    at compile time — replace with real values from developer.whoop.com.
///
/// 2. <b>File import mode</b> — parses a Whoop JSON export (the file produced by
///    Settings → Account → Export Data in the Whoop app) and yields events
///    without any network access. Pass the export file path to
///    <see cref="ImportFileAsync"/>.
///
/// Threading
/// ─────────
/// All API calls are async and use <see cref="CancellationToken"/> for cooperative
/// cancellation. No state is mutated after construction.
/// </summary>
public sealed class WhoopDriver : IBiometricDriver
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string DriverId => "whoop";
    public string DisplayName => "Whoop 4.0";
    public bool SupportsOffline => false; // API mode requires network; import mode is offline

    // ── Whoop API v1 constants ────────────────────────────────────────────────
    // TODO: Replace with values from https://developer.whoop.com once you have a client.

    private const string ApiBaseUrl = "https://api.prod.whoop.com/developer/v1";

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IOAuthTokenStore _tokenStore;
    private readonly HttpClient _http;
    private readonly ILogger<WhoopDriver> _logger;
    private readonly WhoopDriverOptions _options;
    private readonly WhoopAuthenticator _authenticator;

    public WhoopDriver(
        IOAuthTokenStore tokenStore,
        HttpClient httpClient,
        WhoopDriverOptions options,
        WhoopAuthenticator authenticator,
        ILogger<WhoopDriver> logger)
    {
        _tokenStore = tokenStore;
        _http = httpClient;
        _options = options;
        _authenticator = authenticator;
        _logger = logger;
    }

    /// <summary>
    /// Raised when <see cref="FetchSinceAsync"/> skips data because no usable token was
    /// found or an API call failed. The fetch still completes without throwing, so a caller
    /// that keeps a sync watermark needs this to avoid moving it past data it never got.
    /// </summary>
    public event EventHandler? FetchIncomplete;

    // ── IBiometricDriver: availability ────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Returns <c>true</c> when OAuth credentials are configured.</remarks>
    public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
        => ValueTask.FromResult(!string.IsNullOrWhiteSpace(_options.ClientId));

    /// <inheritdoc/>
    /// <remarks>
    /// Whoop uses the OAuth2 authorization-code flow; the interactive browser
    /// consent + token exchange is handled by <see cref="WhoopAuthenticator"/>.
    /// This method only validates that a valid stored token is available;
    /// it does NOT launch a browser.
    /// </remarks>
    public async ValueTask AuthoriseAsync(CancellationToken ct = default)
    {
        var token = await _tokenStore.GetTokenAsync(DriverId, ct).ConfigureAwait(false);
        if (token is null || token.IsExpired)
            throw new UnauthorizedAccessException(
                "No valid Whoop OAuth token found. Complete the OAuth2 flow and store " +
                "a token via IOAuthTokenStore.SaveTokenAsync before calling AuthoriseAsync.");
    }

    // ── IBiometricDriver: historical fetch ────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Fetches recovery, sleep, cycle, and workout records from the Whoop API
    /// for all pages since <paramref name="since"/>.
    /// Requires a valid token in <see cref="IOAuthTokenStore"/>.
    /// </remarks>
    public async IAsyncEnumerable<BiometricEvent> FetchSinceAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await GetValidTokenAsync(ct).ConfigureAwait(false);
        if (token is null)
        {
            _logger.LogWarning("Whoop: No valid OAuth token — skipping API fetch.");
            FetchIncomplete?.Invoke(this, EventArgs.Empty);
            yield break;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var deviceId = _options.DeviceId ?? $"whoop:{_options.ClientId}";

        // ── Recovery ────────────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync(
                           $"{ApiBaseUrl}/recovery",
                           since, token, WhoopJsonContext.Default.WhoopRecoveryList, ct,
                           page => page.NextToken,
                           page => page.Records.SelectMany(r =>
                               WhoopNormalizationMapper.MapRecovery(r, deviceId, correlationId)))
                           .ConfigureAwait(false))
        {
            yield return evt;
        }

        // ── Sleep ────────────────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync(
                           $"{ApiBaseUrl}/sleep",
                           since, token, WhoopJsonContext.Default.WhoopSleepList, ct,
                           page => page.NextToken,
                           page => page.Records.SelectMany(s =>
                               WhoopNormalizationMapper.MapSleep(s, deviceId, correlationId)))
                           .ConfigureAwait(false))
        {
            yield return evt;
        }

        // ── Cycle (Strain) ───────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync(
                           $"{ApiBaseUrl}/cycle",
                           since, token, WhoopJsonContext.Default.WhoopCycleList, ct,
                           page => page.NextToken,
                           page => page.Records.SelectMany(c =>
                               WhoopNormalizationMapper.MapCycle(c, deviceId, correlationId)))
                           .ConfigureAwait(false))
        {
            yield return evt;
        }

        // ── Workouts ─────────────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync(
                           $"{ApiBaseUrl}/workout",
                           since, token, WhoopJsonContext.Default.WhoopWorkoutList, ct,
                           page => page.NextToken,
                           page => page.Records.SelectMany(w =>
                               WhoopNormalizationMapper.MapWorkout(w, deviceId, correlationId)))
                           .ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Whoop does not offer a real-time streaming API; this implementation
    /// polls for new data at a 60-second interval. The Whoop band syncs
    /// to the phone app periodically, so sub-minute granularity is not available
    /// without a wearable SDK integration.
    /// </remarks>
    public async IAsyncEnumerable<BiometricEvent> StreamLiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastFetch = DateTimeOffset.UtcNow.AddMinutes(-5);
        var pollInterval = TimeSpan.FromSeconds(60);

        _logger.LogInformation("Whoop: Starting live-poll stream (60s interval).");

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(pollInterval, ct).ConfigureAwait(false);

            await foreach (var evt in FetchSinceAsync(lastFetch, ct).ConfigureAwait(false))
                yield return evt;

            lastFetch = DateTimeOffset.UtcNow;
        }
    }

    // ── File Import (offline path) ────────────────────────────────────────────

    /// <summary>
    /// Imports biometric events from a Whoop JSON export file.
    ///
    /// Obtain the export from the Whoop app:
    ///   Settings → Account → Download My Data → Receive Data Export Email
    ///
    /// The ZIP archive contains multiple JSON files. Pass the path to the
    /// individual JSON files (e.g., <c>recovery.json</c>, <c>sleep.json</c>,
    /// <c>cycle.json</c>, <c>workout.json</c>).
    /// </summary>
    /// <param name="jsonFilePath">Absolute or relative path to a Whoop export JSON file.</param>
    /// <param name="correlationId">Optional correlation token to group imported events.</param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<BiometricEvent> ImportFileAsync(
        string jsonFilePath,
        string? correlationId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        var deviceId = _options.DeviceId ?? "whoop-import";
        var fileName = Path.GetFileNameWithoutExtension(jsonFilePath).ToLowerInvariant();

        _logger.LogInformation("Whoop: Importing file {File}", Path.GetFileName(jsonFilePath));

        // Routed on the file name; the first route whose key matches wins.
        VendorFileImport.Route[] routes =
        [
            new(["recovery"], (stream, t) => VendorFileImport.ParseAsync(
                stream, WhoopJsonContext.Default.WhoopRecoveryList, list => list.Records,
                r => WhoopNormalizationMapper.MapRecovery(r, deviceId, correlationId), t)),
            new(["sleep"], (stream, t) => VendorFileImport.ParseAsync(
                stream, WhoopJsonContext.Default.WhoopSleepList, list => list.Records,
                s => WhoopNormalizationMapper.MapSleep(s, deviceId, correlationId), t)),
            new(["cycle"], (stream, t) => VendorFileImport.ParseAsync(
                stream, WhoopJsonContext.Default.WhoopCycleList, list => list.Records,
                c => WhoopNormalizationMapper.MapCycle(c, deviceId, correlationId), t)),
            new(["workout"], (stream, t) => VendorFileImport.ParseAsync(
                stream, WhoopJsonContext.Default.WhoopWorkoutList, list => list.Records,
                w => WhoopNormalizationMapper.MapWorkout(w, deviceId, correlationId), t)),
        ];

        await foreach (var evt in VendorFileImport.ReadAsync(
                           jsonFilePath, fileName, routes,
                           name => _logger.LogWarning(
                               "Whoop: Unrecognised export file name '{File}' — skipping.", name),
                           ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    // ── OAuth2 Token helpers ──────────────────────────────────────────────────

    private async ValueTask<OAuthTokenSet?> GetValidTokenAsync(CancellationToken ct)
    {
        var token = await _tokenStore.GetTokenAsync(DriverId, ct).ConfigureAwait(false);
        if (token is null) return null;

        if (token.IsExpired && token.RefreshToken is not null)
            token = await RefreshTokenAsync(token.RefreshToken, ct).ConfigureAwait(false);

        return token;
    }

    private async Task<OAuthTokenSet?> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            var refreshed = await _authenticator.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
            await _tokenStore.SaveTokenAsync(DriverId, refreshed, ct).ConfigureAwait(false);
            return refreshed;
        }
        catch (Exception ex)
        {
            // Refresh can fail if the refresh token was revoked or the network is down.
            // Surface as "no token" so the caller skips the fetch rather than crashing.
            _logger.LogWarning(ex, "Whoop: token refresh failed — re-authorization required.");
            return null;
        }
    }

    // ── Pagination helper ─────────────────────────────────────────────────────

    private async IAsyncEnumerable<BiometricEvent> FetchPagedAsync<TPage>(
        string endpoint,
        DateTimeOffset since,
        OAuthTokenSet token,
        JsonTypeInfo<TPage> pageType,
        [EnumeratorCancellation] CancellationToken ct,
        Func<TPage, string?> getNextToken,
        Func<TPage, IEnumerable<BiometricEvent>> mapPage)
        where TPage : class
    {
        var sinceStr = since.ToUniversalTime().ToString("O");

        var pages = VendorHttpReader.GetPagesAsync(
            _http,
            nextToken => $"{endpoint}?start={Uri.EscapeDataString(sinceStr)}&limit=25"
                       + (nextToken is not null ? $"&nextToken={Uri.EscapeDataString(nextToken)}" : ""),
            token.AccessToken,
            pageType,
            getNextToken,
            (ex, _) =>
            {
                _logger.LogError(ex, "Whoop: API call failed for {Endpoint}", endpoint);
                FetchIncomplete?.Invoke(this, EventArgs.Empty);
            },
            ct);

        await foreach (var page in pages.ConfigureAwait(false))
        {
            foreach (var evt in mapPage(page))
                yield return evt;
        }
    }
}

/// <summary>
/// Configuration options for <see cref="WhoopDriver"/>.
/// Inject via DI; do NOT hard-code credentials in source control.
/// </summary>
public sealed class WhoopDriverOptions
{
    /// <summary>
    /// Whoop OAuth2 client ID from https://developer.whoop.com.
    /// Loaded from configuration (e.g. the <c>WHOOP_API_CLIENT_ID</c> env var / .env file).
    /// Empty until configured — <see cref="WhoopDriver.IsAvailableAsync"/> reports unavailable.
    /// </summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// Whoop OAuth2 client secret (e.g. the <c>WHOOP_API_SECRET</c> env var / .env file).
    /// Empty until configured. Never log this value.
    /// </summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Optional logical device ID to use as the source identifier in ACS events.
    /// Defaults to "whoop:{ClientId}" if not set.
    /// </summary>
    public string? DeviceId { get; set; }
}
