namespace Axon.Core.Ports;

/// <summary>
/// Port for securely storing and retrieving OAuth2 tokens for vendor API access.
///
/// Implementations MUST encrypt token payloads at rest via <see cref="IHardwareVault"/>
/// and MUST NOT log access/refresh token values (PII Shield).
///
/// The token store is keyed by <c>driverId</c> (matching <see cref="IBiometricDriver.DriverId"/>)
/// so each vendor's credentials are isolated.
/// </summary>
public interface IOAuthTokenStore
{
    /// <summary>
    /// Retrieves the stored token set for the given vendor driver.
    /// Returns <c>null</c> if no token exists or the token has been revoked.
    /// </summary>
    ValueTask<OAuthTokenSet?> GetTokenAsync(string driverId, CancellationToken ct = default);

    /// <summary>
    /// Persists a new or refreshed token set for the given vendor driver.
    /// Overwrites any existing token for the same <paramref name="driverId"/>.
    /// </summary>
    ValueTask SaveTokenAsync(string driverId, OAuthTokenSet token, CancellationToken ct = default);

    /// <summary>
    /// Removes all stored tokens for the given driver (e.g., on user disconnect or sign-out).
    /// </summary>
    ValueTask RevokeTokenAsync(string driverId, CancellationToken ct = default);
}

/// <summary>
/// An OAuth2 token set (access token + optional refresh token) for a vendor API.
/// All fields are immutable; obtain a new instance via the token refresh flow.
/// </summary>
/// <param name="AccessToken">Bearer token sent in the Authorization header.</param>
/// <param name="RefreshToken">
///     Optional token used to obtain a new <see cref="AccessToken"/> without user re-authentication.
///     Not all vendors issue refresh tokens.
/// </param>
/// <param name="ExpiresAt">UTC wall-clock time at which the <see cref="AccessToken"/> expires.</param>
/// <param name="Scopes">Optional space-separated list of granted OAuth scopes.</param>
public sealed record OAuthTokenSet(
    string          AccessToken,
    string?         RefreshToken,
    DateTimeOffset  ExpiresAt,
    string?         Scopes = null)
{
    /// <summary>
    /// Returns <c>true</c> if the access token is expired or within 30 seconds of expiry,
    /// indicating that a refresh should be attempted before the next API call.
    /// </summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.AddSeconds(-30);

    /// <summary>PII Shield: suppress raw token values from diagnostic logs.</summary>
    public override string ToString() =>
        $"OAuthTokenSet {{ ExpiresAt={ExpiresAt:O}, IsExpired={IsExpired}, HasRefresh={RefreshToken is not null} }}";
}
