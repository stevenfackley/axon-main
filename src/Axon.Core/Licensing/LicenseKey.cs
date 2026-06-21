using System.Security.Cryptography;
using System.Text;

namespace Axon.Core.Licensing;

/// <summary>
/// Offline, tamper-evident license-key validation using HMAC-SHA256.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Key format (dot-separated, Base64Url-encoded segments):</strong>
/// <c>payload.hmac</c>, where <c>payload</c> is the Base64Url encoding of the
/// UTF-8 string <c>"tier|expiryUnixSeconds"</c> (e.g. <c>"Pro|1893456000"</c>),
/// and <c>hmac</c> is the Base64Url-encoded HMAC-SHA256 of the raw payload bytes
/// computed with <see cref="VerificationSecret"/>.
/// </para>
/// <para>
/// <strong>SECURITY NOTE:</strong> <see cref="VerificationSecret"/> is a placeholder
/// constant. Before any production release, replace it with a secret derived from a
/// hardware key-management system, or switch to Ed25519 asymmetric verification.
/// Rotate the secret if this source code becomes public.
/// </para>
/// <para>
/// This implementation is Native-AOT-safe: it uses only <see cref="HMACSHA256"/>
/// from <c>System.Security.Cryptography</c> with no reflection or dynamic code.
/// </para>
/// </remarks>
public static class LicenseKey
{
    // ── Signing secret ────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ PLACEHOLDER — replace with a securely generated, non-public secret
    /// before shipping. Minimum 32 bytes of high-entropy random data.
    /// Consider deriving this from the hardware TPM / IHardwareVault at runtime
    /// for additional tamper resistance.
    /// </summary>
    private static readonly byte[] VerificationSecret =
        Encoding.UTF8.GetBytes("REPLACE_ME_WITH_A_REAL_32BYTE_SECRET_KEY!!!");

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates an offline license key, returning <see langword="true"/> when
    /// the key is well-formed, untampered, and not expired.
    /// </summary>
    /// <param name="key">The license key string to validate.</param>
    /// <param name="tier">
    /// When the method returns <see langword="true"/>, the <see cref="LicenseTier"/>
    /// encoded in the key; otherwise undefined.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the key is valid and unexpired; <see langword="false"/>
    /// for any invalid, tampered, malformed, or expired key.
    /// </returns>
    public static bool TryValidate(string key, out LicenseTier tier)
    {
        tier = default;

        if (string.IsNullOrEmpty(key))
            return false;

        var dotIndex = key.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= key.Length - 1)
            return false;

        var payloadSegment = key[..dotIndex];
        var hmacSegment    = key[(dotIndex + 1)..];

        // Decode HMAC first — avoids parsing untrusted payload before auth check.
        byte[] providedHmac;
        try
        {
            providedHmac = Base64UrlDecode(hmacSegment);
        }
        catch
        {
            return false;
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlDecode(payloadSegment);
        }
        catch
        {
            return false;
        }

        // Constant-time HMAC comparison to resist timing attacks.
        var expectedHmac = HMACSHA256.HashData(VerificationSecret, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedHmac, providedHmac))
            return false;

        // Payload is authentic — now parse it.
        var payload = Encoding.UTF8.GetString(payloadBytes);
        var pipeIndex = payload.IndexOf('|', StringComparison.Ordinal);
        if (pipeIndex <= 0 || pipeIndex >= payload.Length - 1)
            return false;

        var tierString   = payload[..pipeIndex];
        var expiryString = payload[(pipeIndex + 1)..];

        if (!Enum.TryParse<LicenseTier>(tierString, ignoreCase: false, out var parsedTier))
            return false;

        if (!long.TryParse(expiryString, out var expiryUnix))
            return false;

        var expiry = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
        if (expiry < DateTimeOffset.UtcNow)
            return false;

        tier = parsedTier;
        return true;
    }

    // ── Testing helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Mints a valid license key signed with the internal <see cref="VerificationSecret"/>.
    /// </summary>
    /// <remarks>
    /// <strong>FOR TESTING ONLY.</strong> This method is intentionally public so that
    /// test projects that reference Axon.Core can generate valid keys without needing
    /// access to a private signing service. Do not expose this method in any UI or API.
    /// </remarks>
    /// <param name="tier">The tier to encode in the key.</param>
    /// <param name="expiry">The expiry timestamp to encode.</param>
    /// <returns>A signed license key string in <c>payload.hmac</c> format.</returns>
    public static string MintForTesting(LicenseTier tier, DateTimeOffset expiry)
    {
        var payload      = $"{tier}|{expiry.ToUnixTimeSeconds()}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hmac         = HMACSHA256.HashData(VerificationSecret, payloadBytes);

        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(hmac)}";
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
               .TrimEnd('=')
               .Replace('+', '-')
               .Replace('/', '_');

    private static byte[] Base64UrlDecode(string encoded)
    {
        var s = encoded.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
        }
        return Convert.FromBase64String(s);
    }
}
