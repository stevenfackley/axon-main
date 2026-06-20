using System.Text;
using Axon.Core.Ports;
using Axon.Infrastructure.Security;

namespace Axon.Tests;

/// <summary>
/// Tests the encrypted-at-rest OAuth token store: round-trip persistence,
/// per-driver isolation, encryption (no plaintext on disk), and revocation.
///
/// Uses MockHardwareVault (deterministic key derivation) and a throwaway temp
/// directory so each test is isolated and self-cleaning.
/// </summary>
public sealed class EncryptedFileOAuthTokenStoreTests : IDisposable
{
    private readonly string _dir;

    public EncryptedFileOAuthTokenStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "axon-token-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private EncryptedFileOAuthTokenStore NewStore() =>
        new(new MockHardwareVault(), _dir);

    private static OAuthTokenSet SampleToken() =>
        new(
            AccessToken: "access-abc123",
            RefreshToken: "refresh-xyz789",
            ExpiresAt: new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero),
            Scopes: "read:recovery read:sleep");

    [Fact]
    public async Task SaveThenGet_RoundTripsAllFields()
    {
        var store = NewStore();
        var token = SampleToken();

        await store.SaveTokenAsync("whoop", token);
        var retrieved = await store.GetTokenAsync("whoop");

        Assert.NotNull(retrieved);
        Assert.Equal(token.AccessToken, retrieved!.AccessToken);
        Assert.Equal(token.RefreshToken, retrieved.RefreshToken);
        Assert.Equal(token.ExpiresAt, retrieved.ExpiresAt);
        Assert.Equal(token.Scopes, retrieved.Scopes);
    }

    [Fact]
    public async Task GetToken_UnknownDriver_ReturnsNull()
    {
        var store = NewStore();
        Assert.Null(await store.GetTokenAsync("does-not-exist"));
    }

    [Fact]
    public async Task SavedFile_DoesNotContainPlaintextAccessToken()
    {
        var store = NewStore();
        await store.SaveTokenAsync("whoop", SampleToken());

        // Whatever bytes landed on disk must not contain the raw token text.
        var allBytes = Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        var haystack = Encoding.UTF8.GetString(allBytes);

        Assert.DoesNotContain("access-abc123", haystack);
        Assert.DoesNotContain("refresh-xyz789", haystack);
    }

    [Fact]
    public async Task SaveToken_Overwrites_PreviousTokenForSameDriver()
    {
        var store = NewStore();
        await store.SaveTokenAsync("whoop", SampleToken());

        var updated = SampleToken() with { AccessToken = "access-NEW" };
        await store.SaveTokenAsync("whoop", updated);

        var retrieved = await store.GetTokenAsync("whoop");
        Assert.Equal("access-NEW", retrieved!.AccessToken);
    }

    [Fact]
    public async Task RevokeToken_RemovesIt_SubsequentGetReturnsNull()
    {
        var store = NewStore();
        await store.SaveTokenAsync("whoop", SampleToken());

        await store.RevokeTokenAsync("whoop");

        Assert.Null(await store.GetTokenAsync("whoop"));
    }

    [Fact]
    public async Task Tokens_AreIsolatedPerDriver()
    {
        var store = NewStore();
        await store.SaveTokenAsync("whoop", SampleToken() with { AccessToken = "whoop-tok" });
        await store.SaveTokenAsync("oura", SampleToken() with { AccessToken = "oura-tok" });

        Assert.Equal("whoop-tok", (await store.GetTokenAsync("whoop"))!.AccessToken);
        Assert.Equal("oura-tok", (await store.GetTokenAsync("oura"))!.AccessToken);
    }

    [Fact]
    public async Task Token_WithNullRefreshAndScopes_RoundTrips()
    {
        var store = NewStore();
        var token = new OAuthTokenSet(
            AccessToken: "only-access",
            RefreshToken: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Scopes: null);

        await store.SaveTokenAsync("whoop", token);
        var retrieved = await store.GetTokenAsync("whoop");

        Assert.NotNull(retrieved);
        Assert.Equal("only-access", retrieved!.AccessToken);
        Assert.Null(retrieved.RefreshToken);
        Assert.Null(retrieved.Scopes);
    }
}
