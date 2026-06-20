using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Axon.Core.Ports;

namespace Axon.Infrastructure.Security;

/// <summary>
/// File-based <see cref="IOAuthTokenStore"/> that encrypts each vendor's token set
/// at rest with AES-256-GCM, using a key derived from <see cref="IHardwareVault"/>.
///
/// Storage layout:
///   • One file per driver: <c>{tokenDirectory}/{driverId}.token</c>
///   • File contents: raw bytes of <c>nonce[12] || tag[16] || ciphertext</c>
///     (same authenticated scheme as <c>EncryptionDecorator</c>, sans Base64 —
///     these are opaque binary files, never logged or displayed).
///
/// Serialization (pre-encryption) uses a length-prefixed binary layout via
/// <see cref="BinaryWriter"/>/<see cref="BinaryReader"/> — no JSON, no reflection,
/// so it is Native-AOT safe and robust against any character in token payloads.
///
/// PII Shield: token values are never written in cleartext and never logged.
/// </summary>
public sealed class EncryptedFileOAuthTokenStore : IOAuthTokenStore
{
    private const string TokenKeyLabel = "axon.oauth.tokens";
    private const int NonceSize = 12;   // AesGcm.NonceByteSizes.MaxSize
    private const int TagSize = 16;     // AesGcm.TagByteSizes.MaxSize

    private readonly IHardwareVault _vault;
    private readonly string _tokenDirectory;

    public EncryptedFileOAuthTokenStore(IHardwareVault vault, string tokenDirectory)
    {
        _vault = vault;
        _tokenDirectory = tokenDirectory;
        Directory.CreateDirectory(_tokenDirectory);
    }

    // ── IOAuthTokenStore ──────────────────────────────────────────────────────

    public async ValueTask<OAuthTokenSet?> GetTokenAsync(string driverId, CancellationToken ct = default)
    {
        var path = PathFor(driverId);
        if (!File.Exists(path)) return null;

        var blob = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var plaintext = await DecryptAsync(blob, ct).ConfigureAwait(false);
        return Deserialize(plaintext);
    }

    public async ValueTask SaveTokenAsync(string driverId, OAuthTokenSet token, CancellationToken ct = default)
    {
        var plaintext = Serialize(token);
        var blob = await EncryptAsync(plaintext, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(PathFor(driverId), blob, ct).ConfigureAwait(false);
    }

    public ValueTask RevokeTokenAsync(string driverId, CancellationToken ct = default)
    {
        var path = PathFor(driverId);
        if (File.Exists(path)) File.Delete(path);
        return ValueTask.CompletedTask;
    }

    // ── Paths ─────────────────────────────────────────────────────────────────

    private string PathFor(string driverId)
    {
        // driverId is a controlled identifier; sanitize defensively against path traversal.
        var safe = string.Concat(driverId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safe.Length == 0) safe = "default";
        return Path.Combine(_tokenDirectory, safe + ".token");
    }

    // ── Serialization (length-prefixed binary, AOT-safe) ──────────────────────

    private static byte[] Serialize(OAuthTokenSet token)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(token.AccessToken);
            WriteNullable(w, token.RefreshToken);
            w.Write(token.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
            WriteNullable(w, token.Scopes);
        }
        return ms.ToArray();
    }

    private static OAuthTokenSet Deserialize(byte[] plaintext)
    {
        using var ms = new MemoryStream(plaintext);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var access = r.ReadString();
        var refresh = ReadNullable(r);
        var expiresAt = DateTimeOffset.Parse(
            r.ReadString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var scopes = ReadNullable(r);
        return new OAuthTokenSet(access, refresh, expiresAt, scopes);
    }

    private static void WriteNullable(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null) w.Write(value);
    }

    private static string? ReadNullable(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    // ── AES-256-GCM (nonce || tag || ciphertext) ──────────────────────────────

    private async ValueTask<byte[]> EncryptAsync(byte[] plaintext, CancellationToken ct)
    {
        var keyMem = await _vault.DeriveKeyAsync(TokenKeyLabel, ct).ConfigureAwait(false);
        try
        {
            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var ciphertext = new byte[plaintext.Length];
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(keyMem.Span, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            var blob = new byte[NonceSize + TagSize + ciphertext.Length];
            nonce.CopyTo(blob, 0);
            tag.CopyTo(blob, NonceSize);
            ciphertext.CopyTo(blob, NonceSize + TagSize);
            return blob;
        }
        finally
        {
            _vault.ZeroKey(keyMem);
        }
    }

    private async ValueTask<byte[]> DecryptAsync(byte[] blob, CancellationToken ct)
    {
        var keyMem = await _vault.DeriveKeyAsync(TokenKeyLabel, ct).ConfigureAwait(false);
        try
        {
            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(NonceSize, TagSize);
            var ciphertext = blob.AsSpan(NonceSize + TagSize);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(keyMem.Span, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        finally
        {
            _vault.ZeroKey(keyMem);
        }
    }
}
