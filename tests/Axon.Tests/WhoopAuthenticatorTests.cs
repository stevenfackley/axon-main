using Axon.Infrastructure.Drivers.Whoop;

namespace Axon.Tests;

/// <summary>
/// Tests the pure pieces of the Whoop OAuth2 flow: authorization-URL construction
/// and token-response parsing. The interactive browser/loopback orchestration and
/// live HTTP exchange are verified end-to-end, not here.
/// </summary>
public class WhoopAuthenticatorTests
{
    private const string ClientId = "test-client-id";
    private const string Redirect = "http://localhost:8765/callback";

    // ── Authorization URL ──────────────────────────────────────────────────────

    [Fact]
    public void BuildAuthorizationUrl_TargetsWhoopAuthEndpoint()
    {
        var url = WhoopAuthenticator.BuildAuthorizationUrl(ClientId, Redirect, "state123");
        Assert.StartsWith("https://api.prod.whoop.com/oauth/oauth2/auth?", url);
    }

    [Fact]
    public void BuildAuthorizationUrl_IncludesRequiredOAuthParameters()
    {
        var url = WhoopAuthenticator.BuildAuthorizationUrl(ClientId, Redirect, "state123");
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=test-client-id", url);
        Assert.Contains("state=state123", url);
    }

    [Fact]
    public void BuildAuthorizationUrl_UrlEncodesRedirectUri()
    {
        var url = WhoopAuthenticator.BuildAuthorizationUrl(ClientId, Redirect, "s");
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A8765%2Fcallback", url);
    }

    [Fact]
    public void BuildAuthorizationUrl_RequestsOfflineScope_ForRefreshTokens()
    {
        // Without the 'offline' scope Whoop never issues a refresh token,
        // which would break unattended background sync.
        var url = WhoopAuthenticator.BuildAuthorizationUrl(ClientId, Redirect, "s");
        Assert.Contains("offline", url);
    }

    // ── Token response parsing ─────────────────────────────────────────────────

    [Fact]
    public void ParseTokenResponse_ExtractsAllFields()
    {
        var now = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        const string json = """
            {"access_token":"AT","expires_in":3600,"refresh_token":"RT",
             "scope":"read:sleep offline","token_type":"bearer"}
            """;

        var token = WhoopAuthenticator.ParseTokenResponse(json, now);

        Assert.Equal("AT", token.AccessToken);
        Assert.Equal("RT", token.RefreshToken);
        Assert.Equal("read:sleep offline", token.Scopes);
        Assert.Equal(now.AddSeconds(3600), token.ExpiresAt);
    }

    [Fact]
    public void ParseTokenResponse_NoRefreshToken_YieldsNull()
    {
        var now = DateTimeOffset.UtcNow;
        const string json = """{"access_token":"AT","expires_in":3600,"token_type":"bearer"}""";

        var token = WhoopAuthenticator.ParseTokenResponse(json, now);

        Assert.Equal("AT", token.AccessToken);
        Assert.Null(token.RefreshToken);
        Assert.Null(token.Scopes);
    }

    [Fact]
    public void ParseTokenResponse_ComputesExpiryRelativeToNow()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        const string json = """{"access_token":"AT","expires_in":7200,"token_type":"bearer"}""";

        var token = WhoopAuthenticator.ParseTokenResponse(json, now);

        Assert.Equal(now.AddSeconds(7200), token.ExpiresAt);
    }
}
