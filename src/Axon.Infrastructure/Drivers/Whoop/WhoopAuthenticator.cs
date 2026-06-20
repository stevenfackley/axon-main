using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Axon.Core.Ports;
using Microsoft.Extensions.Logging;

namespace Axon.Infrastructure.Drivers.Whoop;

/// <summary>
/// Drives the Whoop OAuth2 Authorization Code flow for a desktop app, using a
/// loopback redirect (RFC 8252): the app starts a one-shot local HTTP listener,
/// opens the system browser to Whoop's consent screen, and captures the returned
/// authorization code on <see cref="RedirectUri"/>.
///
/// Responsibilities:
///   • Build the authorization URL (incl. the <c>offline</c> scope so Whoop issues
///     a refresh token — without it, unattended sync stops after ~1 hour).
///   • Exchange the authorization code for an <see cref="OAuthTokenSet"/>.
///   • Refresh an expired access token.
///   • Persist tokens via <see cref="IOAuthTokenStore"/> (encrypted at rest).
///
/// PII Shield: access/refresh token values are never logged.
/// AOT-safe: token JSON is read with <see cref="JsonDocument"/> (no reflection).
/// </summary>
public sealed class WhoopAuthenticator
{
    public const string DriverId = "whoop";

    // Loopback redirect — must match the redirect URI registered in the Whoop dashboard.
    public const string RedirectUri = "http://localhost:8765/callback";
    private const string ListenerPrefix = "http://localhost:8765/";

    private const string AuthEndpoint = "https://api.prod.whoop.com/oauth/oauth2/auth";
    private const string TokenEndpoint = "https://api.prod.whoop.com/oauth/oauth2/token";

    // 'offline' is required for a refresh token; the rest grant read access to the data we ingest.
    private const string Scopes =
        "offline read:recovery read:sleep read:workout read:cycles read:body_measurement";

    private readonly WhoopDriverOptions _options;
    private readonly HttpClient _http;
    private readonly IOAuthTokenStore _tokenStore;
    private readonly ILogger<WhoopAuthenticator> _logger;

    public WhoopAuthenticator(
        WhoopDriverOptions options,
        HttpClient http,
        IOAuthTokenStore tokenStore,
        ILogger<WhoopAuthenticator> logger)
    {
        _options = options;
        _http = http;
        _tokenStore = tokenStore;
        _logger = logger;
    }

    // ── Pure helpers (unit-tested) ─────────────────────────────────────────────

    /// <summary>Builds the Whoop OAuth2 authorization-code URL.</summary>
    public static string BuildAuthorizationUrl(string clientId, string redirectUri, string state)
    {
        var query =
            "response_type=code" +
            "&client_id=" + Uri.EscapeDataString(clientId) +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&scope=" + Uri.EscapeDataString(Scopes) +
            "&state=" + Uri.EscapeDataString(state);
        return $"{AuthEndpoint}?{query}";
    }

    /// <summary>
    /// Parses a Whoop OAuth2 token-endpoint JSON response into an <see cref="OAuthTokenSet"/>.
    /// <paramref name="now"/> is the reference time used to compute the absolute expiry from
    /// the relative <c>expires_in</c> seconds.
    /// </summary>
    public static OAuthTokenSet ParseTokenResponse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Whoop token response missing access_token.");
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;

        return new OAuthTokenSet(accessToken, refreshToken, now.AddSeconds(expiresIn), scope);
    }

    // ── Interactive authorization (browser + loopback) ─────────────────────────

    /// <summary>
    /// Runs the full interactive consent flow and persists the resulting token.
    /// Opens the system browser, waits for the loopback redirect, exchanges the
    /// code for a token, and saves it via <see cref="IOAuthTokenStore"/>.
    /// </summary>
    public async Task<OAuthTokenSet> AuthenticateInteractiveAsync(CancellationToken ct = default)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenerPrefix);
        listener.Start();
        _logger.LogInformation("Whoop: awaiting OAuth callback on {Redirect}", RedirectUri);

        try
        {
            OpenBrowser(BuildAuthorizationUrl(_options.ClientId, RedirectUri, state));

            var context = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            var request = context.Request;

            var error = request.QueryString["error"];
            var code = request.QueryString["code"];
            var returnedState = request.QueryString["state"];

            await RespondToBrowserAsync(context.Response, error, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"Whoop authorization was denied: {error}");
            if (returnedState != state)
                throw new InvalidOperationException("Whoop OAuth state mismatch — possible CSRF; aborting.");
            if (string.IsNullOrEmpty(code))
                throw new InvalidOperationException("Whoop OAuth callback returned no authorization code.");

            var token = await ExchangeCodeAsync(code, ct).ConfigureAwait(false);
            await _tokenStore.SaveTokenAsync(DriverId, token, ct).ConfigureAwait(false);
            _logger.LogInformation("Whoop: OAuth token acquired and stored.");
            return token;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Exchanges an authorization code for a token set.</summary>
    public async Task<OAuthTokenSet> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        });
        return await PostTokenRequestAsync(form, ct).ConfigureAwait(false);
    }

    /// <summary>Obtains a fresh token set from a refresh token.</summary>
    public async Task<OAuthTokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["scope"] = "offline",
        });
        return await PostTokenRequestAsync(form, ct).ConfigureAwait(false);
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private async Task<OAuthTokenSet> PostTokenRequestAsync(
        FormUrlEncodedContent form, CancellationToken ct)
    {
        using var response = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseTokenResponse(json, DateTimeOffset.UtcNow);
    }

    private static async Task RespondToBrowserAsync(
        HttpListenerResponse response, string? error, CancellationToken ct)
    {
        var message = string.IsNullOrEmpty(error)
            ? "Whoop connected to Axon. You can close this tab and return to the app."
            : "Whoop authorization failed. You can close this tab and return to the app.";
        var html = $"<!doctype html><html><body style=\"font-family:sans-serif\"><h2>{message}</h2></body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);

        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, ct).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                _logger.LogWarning("Whoop: cannot auto-open browser on this platform. Visit: {Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Whoop: failed to launch browser. Visit the URL manually: {Url}", url);
        }
    }
}
