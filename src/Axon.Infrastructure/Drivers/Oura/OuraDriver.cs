using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Microsoft.Extensions.Logging;

namespace Axon.Infrastructure.Drivers.Oura;

/// <summary>
/// <see cref="IBiometricDriver"/> implementation for the Oura Ring (Generation 3+).
///
/// Data access modes
/// ─────────────────
/// 1. <b>API mode</b> — fetches data from the Oura Ring Cloud API v2 using
///    OAuth2 (Personal Access Token or Authorization Code flow).
///    Requires an active internet connection.
///    Credentials are injected via <see cref="OuraDriverOptions"/>;
///    they are intentionally dummied out — replace with values from
///    https://cloud.ouraring.com/personal-access-tokens (for PAT) or
///    https://cloud.ouraring.com/docs/authentication (for OAuth2 app).
///
/// 2. <b>File import mode</b> — parses JSON exported from the Oura app
///    (Account → Privacy → Download your data) via <see cref="ImportFileAsync"/>.
///
/// API reference: https://cloud.ouraring.com/v2/docs
/// Base URL:      https://api.ouraring.com/v2/usercollection/
/// </summary>
public sealed class OuraDriver : IBiometricDriver
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string DriverId    => "oura";
    public string DisplayName => "Oura Ring";
    public bool   SupportsOffline => false; // API mode requires network; import mode is offline

    // ── Oura API v2 base URL ──────────────────────────────────────────────────

    private const string ApiBaseUrl = "https://api.ouraring.com/v2/usercollection";

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IOAuthTokenStore    _tokenStore;
    private readonly HttpClient          _http;
    private readonly OuraDriverOptions   _options;
    private readonly ILogger<OuraDriver> _logger;

    public OuraDriver(
        IOAuthTokenStore      tokenStore,
        HttpClient            httpClient,
        OuraDriverOptions     options,
        ILogger<OuraDriver>   logger)
    {
        _tokenStore = tokenStore;
        _http       = httpClient;
        _options    = options;
        _logger     = logger;
    }

    // ── IBiometricDriver: availability ────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
        => ValueTask.FromResult(
            !string.IsNullOrWhiteSpace(_options.ClientId) ||
            !string.IsNullOrWhiteSpace(_options.PersonalAccessToken));

    /// <inheritdoc/>
    /// <remarks>
    /// Validates that a usable access token (OAuth2 or Personal Access Token) exists.
    /// Oura supports both:
    ///   - Personal Access Token (simplest): generate at cloud.ouraring.com/personal-access-tokens
    ///   - OAuth2 Authorization Code: for third-party app integration
    /// </remarks>
    public async ValueTask AuthoriseAsync(CancellationToken ct = default)
    {
        // Personal Access Token path — no token store needed
        if (!string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
            return;

        var token = await _tokenStore.GetTokenAsync(DriverId, ct).ConfigureAwait(false);
        if (token is null || token.IsExpired)
            throw new UnauthorizedAccessException(
                "No valid Oura access token found. Set a Personal Access Token in OuraDriverOptions, " +
                "or complete the OAuth2 flow and store a token via IOAuthTokenStore.SaveTokenAsync.");
    }

    // ── IBiometricDriver: historical fetch ────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Fetches readiness, sleep sessions, daily activity, heart rate, and SpO2
    /// from the Oura Ring API for the window [since, now].
    /// </remarks>
    public async IAsyncEnumerable<BiometricEvent> FetchSinceAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "Sync start. Driver={Driver} Since={Since:O}",
            DriverId, since);

        var accessToken = await ResolveAccessTokenAsync(ct).ConfigureAwait(false);
        if (accessToken is null)
        {
            _logger.LogWarning(
                "Sync skipped — no valid access token. Driver={Driver}", DriverId);
            yield break;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var sinceDate     = since.ToString("yyyy-MM-dd");
        var todayDate     = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var ringId        = _options.RingId ?? "oura-ring";

        // ── Daily Readiness ──────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync<OuraDailyReadinessList>(
                           $"{ApiBaseUrl}/daily_readiness?start_date={sinceDate}&end_date={todayDate}",
                           accessToken, ct,
                           page => page.NextToken,
                           page => page.Data.SelectMany(r =>
                               OuraNormalizationMapper.MapDailyReadiness(r, correlationId))))
        {
            yield return evt;
        }

        // ── Sleep Sessions ───────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync<OuraSleepSessionList>(
                           $"{ApiBaseUrl}/sleep?start_date={sinceDate}&end_date={todayDate}",
                           accessToken, ct,
                           page => page.NextToken,
                           page => page.Data.SelectMany(s =>
                               OuraNormalizationMapper.MapSleepSession(s, correlationId))))
        {
            yield return evt;
        }

        // ── Daily Activity ───────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync<OuraDailyActivityList>(
                           $"{ApiBaseUrl}/daily_activity?start_date={sinceDate}&end_date={todayDate}",
                           accessToken, ct,
                           page => page.NextToken,
                           page => page.Data.SelectMany(a =>
                               OuraNormalizationMapper.MapDailyActivity(a, correlationId))))
        {
            yield return evt;
        }

        // ── Continuous Heart Rate ────────────────────────────────────────────
        var sinceIso = since.ToUniversalTime().ToString("O");
        var nowIso   = DateTimeOffset.UtcNow.ToString("O");

        await foreach (var evt in FetchPagedAsync<OuraHeartRateList>(
                           $"{ApiBaseUrl}/heartrate?start_datetime={Uri.EscapeDataString(sinceIso)}" +
                           $"&end_datetime={Uri.EscapeDataString(nowIso)}",
                           accessToken, ct,
                           page => page.NextToken,
                           page => page.Data.Select(hr =>
                               OuraNormalizationMapper.MapHeartRateSample(hr, ringId, correlationId))))
        {
            yield return evt;
        }

        // ── Daily SpO2 ───────────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync<OuraSpO2List>(
                           $"{ApiBaseUrl}/spo2?start_date={sinceDate}&end_date={todayDate}",
                           accessToken, ct,
                           page => page.NextToken,
                           page =>
                           {
                               var events = new List<BiometricEvent>();
                               foreach (var s in page.Data)
                               {
                                   var e = OuraNormalizationMapper.MapSpO2Daily(s, ringId, correlationId);
                                   if (e is not null) events.Add(e);
                               }
                               return events;
                           }))
        {
            yield return evt;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Oura does not offer a WebSocket / push stream for real-time data;
    /// this implementation polls at 5-minute intervals.
    /// The Oura Ring syncs to the phone app periodically (typically every few minutes).
    /// </remarks>
    public async IAsyncEnumerable<BiometricEvent> StreamLiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastFetch    = DateTimeOffset.UtcNow.AddMinutes(-10);
        var pollInterval = TimeSpan.FromMinutes(5);

        _logger.LogInformation("Oura: Starting live-poll stream (5-min interval).");

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
    /// Imports biometric events from an Oura Ring JSON export file.
    ///
    /// To export from Oura app:
    ///   Account → Privacy → Download your data
    ///
    /// The export ZIP contains multiple JSON files (sleep.json, activity.json,
    /// readiness.json, heart_rate.json, etc.) with structures matching the Oura API v2.
    /// </summary>
    /// <param name="jsonFilePath">Absolute path to an Oura export JSON file.</param>
    /// <param name="correlationId">Optional correlation token.</param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<BiometricEvent> ImportFileAsync(
        string  jsonFilePath,
        string? correlationId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        var ringId      = _options.RingId ?? "oura-import";
        var fileName    = Path.GetFileNameWithoutExtension(jsonFilePath).ToLowerInvariant();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        _logger.LogInformation("Oura: Importing file {File}", Path.GetFileName(jsonFilePath));

        await using var stream = File.OpenRead(jsonFilePath);

        if (fileName.Contains("readiness"))
        {
            var list = await JsonSerializer.DeserializeAsync<OuraDailyReadinessList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var r in list.Data)
                    foreach (var evt in OuraNormalizationMapper.MapDailyReadiness(r, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("sleep"))
        {
            var list = await JsonSerializer.DeserializeAsync<OuraSleepSessionList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var s in list.Data)
                    foreach (var evt in OuraNormalizationMapper.MapSleepSession(s, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("activit"))
        {
            var list = await JsonSerializer.DeserializeAsync<OuraDailyActivityList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var a in list.Data)
                    foreach (var evt in OuraNormalizationMapper.MapDailyActivity(a, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("heart"))
        {
            var list = await JsonSerializer.DeserializeAsync<OuraHeartRateList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var hr in list.Data)
                    yield return OuraNormalizationMapper.MapHeartRateSample(hr, ringId, correlationId);
        }
        else if (fileName.Contains("spo2"))
        {
            var list = await JsonSerializer.DeserializeAsync<OuraSpO2List>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var s in list.Data)
                {
                    var evt = OuraNormalizationMapper.MapSpO2Daily(s, ringId, correlationId);
                    if (evt is not null) yield return evt;
                }
        }
        else
        {
            _logger.LogWarning("Oura: Unrecognised export file name '{File}' — skipping.", fileName);
        }
    }

    // ── Token helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the best available access token:
    /// Personal Access Token takes priority over OAuth2 stored token.
    /// </summary>
    private async ValueTask<string?> ResolveAccessTokenAsync(CancellationToken ct)
    {
        // Personal Access Token (static, no expiry managed here)
        if (!string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
            return _options.PersonalAccessToken;

        // OAuth2 stored token
        var token = await _tokenStore.GetTokenAsync(DriverId, ct).ConfigureAwait(false);
        if (token is null) return null;

        if (token.IsExpired && token.RefreshToken is not null)
            token = await RefreshTokenAsync(token.RefreshToken, ct).ConfigureAwait(false);

        return token?.AccessToken;
    }

    private async Task<OAuthTokenSet?> RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        // TODO: Implement OAuth2 token refresh.
        // POST https://api.ouraring.com/oauth/token
        // Body: grant_type=refresh_token&refresh_token={refreshToken}
        //       &client_id={_options.ClientId}&client_secret={_options.ClientSecret}
        _logger.LogWarning("Oura: Token refresh not yet implemented — token expired.");
        return null;
    }

    // ── Pagination helper ─────────────────────────────────────────────────────

    private async IAsyncEnumerable<BiometricEvent> FetchPagedAsync<TPage>(
        string                              url,
        string                              accessToken,
        [EnumeratorCancellation] CancellationToken ct,
        Func<TPage, string?>                getNextToken,
        Func<TPage, IEnumerable<BiometricEvent>> mapPage)
        where TPage : class
    {
        var pageUrl = url;

        do
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oura: API call failed for {Url}", pageUrl);
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var page = await JsonSerializer.DeserializeAsync<TPage>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct).ConfigureAwait(false);

            if (page is null) yield break;

            foreach (var evt in mapPage(page))
                yield return evt;

            var nextToken = getNextToken(page);
            if (nextToken is null) break;

            // Oura uses next_token as a cursor — append to original URL
            pageUrl = url.Contains('?')
                ? $"{url}&next_token={Uri.EscapeDataString(nextToken)}"
                : $"{url}?next_token={Uri.EscapeDataString(nextToken)}";

        } while (true);
    }
}

/// <summary>
/// Configuration options for <see cref="OuraDriver"/>.
/// Inject via DI; do NOT hard-code credentials in source control.
/// </summary>
public sealed class OuraDriverOptions
{
    /// <summary>
    /// Oura Personal Access Token — simplest authentication method for single-user use.
    /// Generate at: https://cloud.ouraring.com/personal-access-tokens
    /// DUMMY VALUE — replace with a real PAT before use.
    /// </summary>
    public string? PersonalAccessToken { get; set; } = null; // Set this to your PAT

    /// <summary>
    /// Oura OAuth2 client ID (for multi-user / app use).
    /// Register at: https://cloud.ouraring.com/oauth/applications
    /// DUMMY VALUE — replace with real credentials before use.
    /// </summary>
    public string ClientId { get; set; } = "OURA_CLIENT_ID_PLACEHOLDER";

    /// <summary>
    /// Oura OAuth2 client secret.
    /// DUMMY VALUE — replace with real credentials before use.
    /// </summary>
    public string ClientSecret { get; set; } = "OURA_CLIENT_SECRET_PLACEHOLDER";

    /// <summary>
    /// OAuth2 authorization URL for the Oura Ring app.
    /// Direct users here to begin the authorization code flow.
    /// </summary>
    public string AuthorizationUrl { get; set; } =
        "https://cloud.ouraring.com/oauth/authorize" +
        "?response_type=code" +
        "&scope=email+personal+daily+heartrate+workout+tag+session+spo2+ring_configuration" +
        "&client_id=OURA_CLIENT_ID_PLACEHOLDER";

    /// <summary>
    /// Logical Oura Ring identifier (used as device ID in ACS events).
    /// Defaults to "oura-ring" if not set.
    /// </summary>
    public string? RingId { get; set; }
}
