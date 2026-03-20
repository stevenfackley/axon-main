using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

    public string DriverId    => "garmin";
    public string DisplayName => "Garmin Connect";
    public bool   SupportsOffline => false; // API mode requires network; import mode is offline

    // ── Garmin Health API base URL ────────────────────────────────────────────
    // TODO: Update to production endpoint when Health API access is approved.

    private const string ApiBaseUrl = "https://apis.garmin.com/wellness-api/rest";

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IOAuthTokenStore      _tokenStore;
    private readonly HttpClient            _http;
    private readonly GarminDriverOptions   _options;
    private readonly ILogger<GarminDriver> _logger;

    public GarminDriver(
        IOAuthTokenStore       tokenStore,
        HttpClient             httpClient,
        GarminDriverOptions    options,
        ILogger<GarminDriver>  logger)
    {
        _tokenStore = tokenStore;
        _http       = httpClient;
        _options    = options;
        _logger     = logger;
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
        var startEpoch    = since.ToUnixTimeSeconds();
        var endEpoch      = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ── Daily Summaries ──────────────────────────────────────────────────
        var dailyUrl = $"{ApiBaseUrl}/dailies?uploadStartTimeInSeconds={startEpoch}" +
                       $"&uploadEndTimeInSeconds={endEpoch}";

        await foreach (var evt in FetchAndMapAsync<GarminDailySummaryList>(
                           dailyUrl, token, ct,
                           list => list.Dailies.SelectMany(d =>
                               GarminNormalizationMapper.MapDailySummary(d, correlationId))))
        {
            yield return evt;
        }

        // ── Sleep Summaries ──────────────────────────────────────────────────
        var sleepUrl = $"{ApiBaseUrl}/sleeps?uploadStartTimeInSeconds={startEpoch}" +
                       $"&uploadEndTimeInSeconds={endEpoch}";

        await foreach (var evt in FetchAndMapAsync<GarminSleepSummaryList>(
                           sleepUrl, token, ct,
                           list => list.Sleeps.SelectMany(s =>
                               GarminNormalizationMapper.MapSleepSummary(s, correlationId))))
        {
            yield return evt;
        }

        // ── HRV Summaries ────────────────────────────────────────────────────
        var hrvUrl = $"{ApiBaseUrl}/hrv?uploadStartTimeInSeconds={startEpoch}" +
                     $"&uploadEndTimeInSeconds={endEpoch}";

        await foreach (var evt in FetchAndMapAsync<GarminHrvSummaryList>(
                           hrvUrl, token, ct,
                           list => list.HrvSummaries.SelectMany(h =>
                               GarminNormalizationMapper.MapHrvSummary(h, correlationId))))
        {
            yield return evt;
        }

        // ── Body Composition ─────────────────────────────────────────────────
        var bodyUrl = $"{ApiBaseUrl}/bodyComps?uploadStartTimeInSeconds={startEpoch}" +
                      $"&uploadEndTimeInSeconds={endEpoch}";

        await foreach (var evt in FetchAndMapAsync<GarminBodyCompositionList>(
                           bodyUrl, token, ct,
                           list => list.Compositions.SelectMany(c =>
                               GarminNormalizationMapper.MapBodyComposition(c, correlationId))))
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
        var lastFetch    = DateTimeOffset.UtcNow.AddMinutes(-10);
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
        string  jsonFilePath,
        string? correlationId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileNameWithoutExtension(jsonFilePath).ToLowerInvariant();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        _logger.LogInformation("Garmin: Importing file {File}", Path.GetFileName(jsonFilePath));

        await using var stream = File.OpenRead(jsonFilePath);

        if (fileName.Contains("daily") || fileName.Contains("activit"))
        {
            var list = await JsonSerializer.DeserializeAsync<GarminDailySummaryList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var d in list.Dailies)
                    foreach (var evt in GarminNormalizationMapper.MapDailySummary(d, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("sleep"))
        {
            var list = await JsonSerializer.DeserializeAsync<GarminSleepSummaryList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var s in list.Sleeps)
                    foreach (var evt in GarminNormalizationMapper.MapSleepSummary(s, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("hrv"))
        {
            var list = await JsonSerializer.DeserializeAsync<GarminHrvSummaryList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var h in list.HrvSummaries)
                    foreach (var evt in GarminNormalizationMapper.MapHrvSummary(h, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("body") || fileName.Contains("weight"))
        {
            var list = await JsonSerializer.DeserializeAsync<GarminBodyCompositionList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var c in list.Compositions)
                    foreach (var evt in GarminNormalizationMapper.MapBodyComposition(c, correlationId))
                        yield return evt;
        }
        else
        {
            _logger.LogWarning("Garmin: Unrecognised export file name '{File}' — skipping.", fileName);
        }
    }

    // ── HTTP fetch helpers ────────────────────────────────────────────────────

    private async IAsyncEnumerable<BiometricEvent> FetchAndMapAsync<TResponse>(
        string                                    url,
        OAuthTokenSet                             token,
        [EnumeratorCancellation] CancellationToken ct,
        Func<TResponse, IEnumerable<BiometricEvent>> map)
        where TResponse : class
    {
        ct.ThrowIfCancellationRequested();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Garmin Health API uses OAuth2 bearer tokens for server-to-server calls.
        // Note: user-facing Garmin Connect uses OAuth 1.0a; Health API partner access
        // uses OAuth 2.0. Adjust header format once access is provisioned.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Garmin: API call failed for {Url}", url);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<TResponse>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            ct).ConfigureAwait(false);

        if (result is null) yield break;

        foreach (var evt in map(result))
            yield return evt;
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
    /// DUMMY VALUE — replace with real credentials before use.
    /// </summary>
    public string ConsumerKey { get; set; } = "GARMIN_CONSUMER_KEY_PLACEHOLDER";

    /// <summary>
    /// Garmin Health API consumer secret.
    /// DUMMY VALUE — replace with real credentials before use.
    /// </summary>
    public string ConsumerSecret { get; set; } = "GARMIN_CONSUMER_SECRET_PLACEHOLDER";

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
