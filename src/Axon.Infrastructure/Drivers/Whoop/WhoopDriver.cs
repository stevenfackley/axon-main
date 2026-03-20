using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

    public string DriverId    => "whoop";
    public string DisplayName => "Whoop 4.0";
    public bool   SupportsOffline => false; // API mode requires network; import mode is offline

    // ── Whoop API v1 constants ────────────────────────────────────────────────
    // TODO: Replace with values from https://developer.whoop.com once you have a client.

    private const string ApiBaseUrl = "https://api.prod.whoop.com/developer/v1";

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IOAuthTokenStore   _tokenStore;
    private readonly HttpClient         _http;
    private readonly ILogger<WhoopDriver> _logger;
    private readonly WhoopDriverOptions _options;

    public WhoopDriver(
        IOAuthTokenStore      tokenStore,
        HttpClient            httpClient,
        WhoopDriverOptions    options,
        ILogger<WhoopDriver>  logger)
    {
        _tokenStore = tokenStore;
        _http       = httpClient;
        _options    = options;
        _logger     = logger;
    }

    // ── IBiometricDriver: availability ────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Returns <c>true</c> when OAuth credentials are configured.</remarks>
    public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
        => ValueTask.FromResult(!string.IsNullOrWhiteSpace(_options.ClientId));

    /// <inheritdoc/>
    /// <remarks>
    /// Whoop uses OAuth2 Authorization Code flow.
    /// Direct the user to <see cref="WhoopDriverOptions.AuthorizationUrl"/> in a browser,
    /// then exchange the returned code for tokens using your backend or local callback server.
    /// This method validates that a valid stored token is available;
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
            yield break;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var deviceId      = _options.DeviceId ?? $"whoop:{_options.ClientId}";

        // ── Recovery ────────────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync<WhoopRecoveryList>(
                           $"{ApiBaseUrl}/recovery",
                           since, token, ct,
                           page => page.NextToken,
                           page => page.Records.SelectMany(r =>
                               WhoopNormalizationMapper.MapRecovery(r, deviceId, correlationId))))
        {
            yield return evt;
        }

        // ── Sleep ────────────────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync<WhoopSleepList>(
                           $"{ApiBaseUrl}/sleep",
                           since, token, ct,
                           page => page.NextToken,
                           page => page.Records.SelectMany(s =>
                               WhoopNormalizationMapper.MapSleep(s, deviceId, correlationId))))
        {
            yield return evt;
        }

        // ── Cycle (Strain) ───────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync<WhoopCycleList>(
                           $"{ApiBaseUrl}/cycle",
                           since, token, ct,
                           page => page.NextToken,
                           page => page.Records.SelectMany(c =>
                               WhoopNormalizationMapper.MapCycle(c, deviceId, correlationId))))
        {
            yield return evt;
        }

        // ── Workouts ─────────────────────────────────────────────────────────
        await foreach (var evt in FetchPagedAsync<WhoopWorkoutList>(
                           $"{ApiBaseUrl}/workout",
                           since, token, ct,
                           page => page.NextToken,
                           page => page.Records.SelectMany(w =>
                               WhoopNormalizationMapper.MapWorkout(w, deviceId, correlationId))))
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
        string  jsonFilePath,
        string? correlationId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        var deviceId = _options.DeviceId ?? "whoop-import";
        var fileName = Path.GetFileNameWithoutExtension(jsonFilePath).ToLowerInvariant();

        _logger.LogInformation("Whoop: Importing file {File}", Path.GetFileName(jsonFilePath));

        await using var stream = File.OpenRead(jsonFilePath);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (fileName.Contains("recovery"))
        {
            var list = await JsonSerializer.DeserializeAsync<WhoopRecoveryList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var r in list.Records)
                    foreach (var evt in WhoopNormalizationMapper.MapRecovery(r, deviceId, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("sleep"))
        {
            var list = await JsonSerializer.DeserializeAsync<WhoopSleepList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var s in list.Records)
                    foreach (var evt in WhoopNormalizationMapper.MapSleep(s, deviceId, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("cycle"))
        {
            var list = await JsonSerializer.DeserializeAsync<WhoopCycleList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var c in list.Records)
                    foreach (var evt in WhoopNormalizationMapper.MapCycle(c, deviceId, correlationId))
                        yield return evt;
        }
        else if (fileName.Contains("workout"))
        {
            var list = await JsonSerializer.DeserializeAsync<WhoopWorkoutList>(
                stream, jsonOptions, ct).ConfigureAwait(false);
            if (list is not null)
                foreach (var w in list.Records)
                    foreach (var evt in WhoopNormalizationMapper.MapWorkout(w, deviceId, correlationId))
                        yield return evt;
        }
        else
        {
            _logger.LogWarning("Whoop: Unrecognised export file name '{File}' — skipping.", fileName);
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
        // TODO: Implement OAuth2 token refresh once client credentials are available.
        // POST https://api.prod.whoop.com/oauth/oauth2/token
        // Body: grant_type=refresh_token&refresh_token={refreshToken}
        //       &client_id={_options.ClientId}&client_secret={_options.ClientSecret}
        _logger.LogWarning("Whoop: Token refresh not yet implemented — token expired.");
        return null;
    }

    // ── Pagination helper ─────────────────────────────────────────────────────

    private async IAsyncEnumerable<BiometricEvent> FetchPagedAsync<TPage>(
        string                              endpoint,
        DateTimeOffset                      since,
        OAuthTokenSet                       token,
        [EnumeratorCancellation] CancellationToken ct,
        Func<TPage, string?>                getNextToken,
        Func<TPage, IEnumerable<BiometricEvent>> mapPage)
        where TPage : class
    {
        string? nextToken = null;
        var sinceStr = since.ToUniversalTime().ToString("O");

        do
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{endpoint}?start={Uri.EscapeDataString(sinceStr)}&limit=25"
                    + (nextToken is not null ? $"&nextToken={Uri.EscapeDataString(nextToken)}" : "");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Whoop: API call failed for {Endpoint}", endpoint);
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

            nextToken = getNextToken(page);

        } while (nextToken is not null);
    }
}

/// <summary>
/// Configuration options for <see cref="WhoopDriver"/>.
/// Inject via DI; do NOT hard-code credentials in source control.
/// </summary>
public sealed class WhoopDriverOptions
{
    /// <summary>
    /// Whoop OAuth2 client ID from https://developer.whoop.com
    /// Set via app configuration / secrets manager.
    /// DUMMY VALUE — replace with real credentials before use.
    /// </summary>
    public string ClientId { get; set; } = "WHOOP_CLIENT_ID_PLACEHOLDER";

    /// <summary>
    /// Whoop OAuth2 client secret.
    /// DUMMY VALUE — replace with real credentials before use.
    /// </summary>
    public string ClientSecret { get; set; } = "WHOOP_CLIENT_SECRET_PLACEHOLDER";

    /// <summary>
    /// OAuth2 authorization endpoint for the Whoop developer portal.
    /// Direct users here to begin the OAuth2 authorization code flow.
    /// </summary>
    public string AuthorizationUrl { get; set; } =
        "https://api.prod.whoop.com/oauth/oauth2/auth" +
        "?response_type=code" +
        "&scope=read:recovery%20read:sleep%20read:workout%20read:cycles%20read:body_measurement" +
        "&client_id=WHOOP_CLIENT_ID_PLACEHOLDER";

    /// <summary>
    /// Optional logical device ID to use as the source identifier in ACS events.
    /// Defaults to "whoop:{ClientId}" if not set.
    /// </summary>
    public string? DeviceId { get; set; }
}
