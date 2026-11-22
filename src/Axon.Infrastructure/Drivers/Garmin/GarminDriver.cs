using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Microsoft.Extensions.Logging;

namespace Axon.Infrastructure.Drivers.Garmin;

/// <summary>
/// <see cref="IBiometricDriver"/> implementation for Garmin Connect / Health API.
///
/// Data access modes
/// ─────────────────
/// 1. <b>API mode</b> — fetches data from the Garmin Health API v1 using OAuth2.
///    Requires registration as a Garmin Health API partner and an active internet connection.
///    Credentials are injected via <see cref="GarminDriverOptions"/>;
///    they are intentionally dummied out — replace with real values from
///    https://developer.garmin.com/gc-developer-program/health-api/
///
/// 2. <b>File import mode</b> — parses JSON files exported from Garmin Connect
///    (Export via Garmin Connect website → Dashboard → Export Data).
///    Passes through <see cref="ImportFileAsync"/> with no network access.
///
/// Notes on Garmin Health API
/// ──────────────────────────
/// The Garmin Health API uses a push model: Garmin delivers data to a registered
/// webhook endpoint. The pull endpoints used here are available for testing and
/// backfill scenarios. Production use requires webhook registration.
///
/// API reference: https://developer.garmin.com/gc-developer-program/health-api/
/// </summary>
public sealed class GarminDriver : IBiometricDriver
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string DriverId => "garmin";
    public string DisplayName => "Garmin Connect";
    public bool SupportsOffline => false; // API mode requires network; import mode is offline

    // ── Garmin Health API base URL ────────────────────────────────────────────
    // TODO: Update to production endpoint when Health API access is approved.

    private const string ApiBaseUrl = "https://apis.garmin.com/wellness-api/rest";

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IOAuthTokenStore _tokenStore;
    private readonly HttpClient _http;
    private readonly GarminDriverOptions _options;
    private readonly ILogger<GarminDriver> _logger;

    public GarminDriver(
        IOAuthTokenStore tokenStore,
        HttpClient httpClient,
        GarminDriverOptions options,
        ILogger<GarminDriver> logger)
    {
        _tokenStore = tokenStore;
        _http = httpClient;
        _options = options;
        _logger = logger;
    }

    // ── IBiometricDriver: availability ────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
        => ValueTask.FromResult(!string.IsNullOrWhiteSpace(_options.ConsumerKey));

    /// <inheritdoc/>
    /// <remarks>
    /// Validates that a stored OAuth token exists for this driver.
    /// Garmin uses OAuth 1.0a for user tokens; the authorization flow must
    /// be completed out-of-band (Garmin Connect app or web).
    /// </remarks>
    public async ValueTask AuthoriseAsync(CancellationToken ct = default)
    {
        var token = await _tokenStore.GetTokenAsync(DriverId, ct).ConfigureAwait(false);
        if (token is null)
            throw new UnauthorizedAccessException(
                "No Garmin OAuth token found. Complete the Garmin Health API OAuth flow " +
                "and store a token via IOAuthTokenStore.SaveTokenAsync.");
    }

    // ── IBiometricDriver: historical fetch ────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Pulls daily summaries, sleep summaries, HRV summaries, and body composition
    /// from the Garmin Health API for the window [since, now].
    /// The Garmin Health API uses Unix epoch timestamps for its date range parameters.
    /// </remarks>
    public async IAsyncEnumerable<BiometricEvent> FetchSinceAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await _tokenStore.GetTokenAsync(DriverId, ct).ConfigureAwait(false);
        if (token is null)
        {
            _logger.LogWarning("Garmin: No valid OAuth token — skipping API fetch.");
            yield break;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var startEpoch = since.ToUnixTimeSeconds();
        var endEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ── Daily Summaries ──────────────────────────────────────────────────
        var dailyUrl = $"{ApiBaseUrl}/dailies?uploadStartTimeInSeconds={startEpoch}" +
                       $"&uploadEndTimeInSeconds={endEpoch}";

        await foreach (var evt in FetchAndMapAsync(
                           dailyUrl, token, GarminJsonContext.Default.GarminDailySummaryList, ct,
                           list => list.Dailies.SelectMany(d =>
                               GarminNormalizationMapper.MapDailySummary(d, correlationId)))
                           .ConfigureAwait(false))
        {
            yield return evt;
        }

        // ── Sleep Summaries ──────────────────────────────────────────────────
        var sleepUrl = $"{ApiBaseUrl}/sleeps?uploadStartTimeInSeconds={startEpoch}" +
                       $"&uploadEndTimeInSeconds={endEpoch}";

        await foreach (var evt in FetchAndMapAsync(
                           sleepUrl, token, GarminJsonContext.Default.GarminSleepSummaryList, ct,
                           list => list.Sleeps.SelectMany(s =>
                               GarminNormalizationMapper.MapSleepSummary(s, correlationId)))
                           .ConfigureAwait(false))
        {
            yield return evt;
        }

        // ── HRV Summaries ────────────────────────────────────────────────────
        var hrvUrl = $"{ApiBaseUrl}/hrv?uploadStartTimeInSeconds={startEpoch}" +
                     $"&uploadEndTimeInSeconds={endEpoch}";

        await foreach (var evt in FetchAndMapAsync(
                           hrvUrl, token, GarminJsonContext.Default.GarminHrvSummaryList, ct,
                           list => list.HrvSummaries.SelectMany(h =>
                               GarminNormalizationMapper.MapHrvSummary(h, correlationId)))
                           .ConfigureAwait(false))
        {
            yield return evt;
        }

        // ── Body Composition ─────────────────────────────────────────────────
        var bodyUrl = $"{ApiBaseUrl}/bodyComps?uploadStartTimeInSeconds={startEpoch}" +
                      $"&uploadEndTimeInSeconds={endEpoch}";

        await foreach (var evt in FetchAndMapAsync(
                           bodyUrl, token, GarminJsonContext.Default.GarminBodyCompositionList, ct,
                           list => list.Compositions.SelectMany(c =>
                               GarminNormalizationMapper.MapBodyComposition(c, correlationId)))
                           .ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The Garmin Health API uses a push-webhook model for real-time data.
    /// This implementation polls at 5-minute intervals as a fallback.
    /// For production real-time data, register a webhook endpoint and have
    /// Garmin push directly to your server.
    /// </remarks>
    public async IAsyncEnumerable<BiometricEvent> StreamLiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastFetch = DateTimeOffset.UtcNow.AddMinutes(-10);
        var pollInterval = TimeSpan.FromMinutes(5);

        _logger.LogInformation("Garmin: Starting live-poll stream (5-min interval).");

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
    /// Imports biometric events from a Garmin Connect JSON export file.
    ///
    /// To export from Garmin Connect:
    ///   Sign in at https://connect.garmin.com → Dashboard → Export Your Data
    ///
    /// The export archive contains activity JSON, health stats, and more.
    /// Pass individual JSON files that contain the Health API structure
    /// (daily summaries, sleep summaries, HRV, body composition).
    /// </summary>
    /// <param name="jsonFilePath">Absolute path to a Garmin Connect export JSON file.</param>
    /// <param name="correlationId">Optional correlation token.</param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<BiometricEvent> ImportFileAsync(
        string jsonFilePath,
        string? correlationId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileNameWithoutExtension(jsonFilePath).ToLowerInvariant();

        _logger.LogInformation("Garmin: Importing file {File}", Path.GetFileName(jsonFilePath));

        // Routed on the file name; the first route whose key matches wins.
        VendorFileImport.Route[] routes =
        [
            new(["daily", "activit"], (stream, t) => VendorFileImport.ParseAsync(
                stream, GarminJsonContext.Default.GarminDailySummaryList, list => list.Dailies,
                d => GarminNormalizationMapper.MapDailySummary(d, correlationId), t)),
            new(["sleep"], (stream, t) => VendorFileImport.ParseAsync(
                stream, GarminJsonContext.Default.GarminSleepSummaryList, list => list.Sleeps,
                s => GarminNormalizationMapper.MapSleepSummary(s, correlationId), t)),
            new(["hrv"], (stream, t) => VendorFileImport.ParseAsync(
                stream, GarminJsonContext.Default.GarminHrvSummaryList, list => list.HrvSummaries,
                h => GarminNormalizationMapper.MapHrvSummary(h, correlationId), t)),
            new(["body", "weight"], (stream, t) => VendorFileImport.ParseAsync(
                stream, GarminJsonContext.Default.GarminBodyCompositionList, list => list.Compositions,
                c => GarminNormalizationMapper.MapBodyComposition(c, correlationId), t)),
        ];

        await foreach (var evt in VendorFileImport.ReadAsync(
                           jsonFilePath, fileName, routes,
                           name => _logger.LogWarning(
                               "Garmin: Unrecognised export file name '{File}' — skipping.", name),
                           ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    // ── HTTP fetch helpers ────────────────────────────────────────────────────

    private async IAsyncEnumerable<BiometricEvent> FetchAndMapAsync<TResponse>(
        string url,
        OAuthTokenSet token,
        JsonTypeInfo<TResponse> responseType,
        [EnumeratorCancellation] CancellationToken ct,
        Func<TResponse, IEnumerable<BiometricEvent>> map)
        where TResponse : class
    {
        // Garmin Health API uses OAuth2 bearer tokens for server-to-server calls.
        // Note: user-facing Garmin Connect uses OAuth 1.0a; Health API partner access
        // uses OAuth 2.0. Adjust header format once access is provisioned.
        // The responses are not paged, so there is never a next cursor.
        var responses = VendorHttpReader.GetPagesAsync(
            _http,
            _ => url,
            token.AccessToken,
            responseType,
            _ => null,
            (ex, _) => _logger.LogError(ex, "Garmin: API call failed for {Url}", url),
            ct);

        await foreach (var result in responses.ConfigureAwait(false))
        {
            foreach (var evt in map(result))
                yield return evt;
        }
    }
}

/// <summary>
/// Configuration options for <see cref="GarminDriver"/>.
/// Inject via DI; do NOT hard-code credentials in source control.
/// </summary>
public sealed class GarminDriverOptions
{
    /// <summary>
    /// Garmin Health API consumer key (OAuth1) or client ID (OAuth2).
    /// Obtain from https://developer.garmin.com/gc-developer-program/
    /// Empty until configured — <see cref="GarminDriver.IsAvailableAsync"/> reports unavailable.
    /// </summary>
    public string ConsumerKey { get; set; } = "";

    /// <summary>
    /// Garmin Health API consumer secret. Empty until configured. Never log this value.
    /// </summary>
    public string ConsumerSecret { get; set; } = "";

    /// <summary>
    /// Garmin OAuth2 authorization endpoint (for Health API partner accounts).
    /// </summary>
    public string AuthorizationUrl { get; set; } =
        "https://connect.garmin.com/oauthConfirm";

    /// <summary>
    /// Optional logical device ID. Defaults to "garmin:{ConsumerKey}" if not set.
    /// </summary>
    public string? DeviceId { get; set; }
}
