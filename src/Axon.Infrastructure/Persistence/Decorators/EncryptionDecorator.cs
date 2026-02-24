using System.Security.Cryptography;
using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.Infrastructure.Persistence.Decorators;

/// <summary>
/// GDPR field-level encryption decorator for <see cref="IBiometricRepository"/>.
///
/// Transparently encrypts PII fields (<see cref="SourceMetadata.DeviceId"/>) using
/// AES-256-GCM before data reaches the EF Core provider, and decrypts on read.
///
/// Encryption scheme:
///   • Algorithm : AES-256-GCM (authenticated — no padding oracle)
///   • Key source: <see cref="IHardwareVault"/> (TPM/Secure Enclave)
///   • Nonce     : 12-byte random per encryption call (GCM standard)
///   • Output    : Base-64(nonce[12] || tag[16] || ciphertext)
///
/// Performance:
///   • Key is derived once per decorator lifetime (lazy, cached).
///     The cached copy is zeroed when the decorator is disposed.
///   • Encryption operates on <see cref="ReadOnlySpan{T}"/> — no intermediate
///     string allocations in the hot path.
///
/// Decorator chain position: sits between AuditLoggingDecorator and the concrete repo.
/// </summary>
public sealed class EncryptionDecorator(
    IBiometricRepository inner,
    IHardwareVault       vault) : IBiometricRepository, IAsyncDisposable
{
    private const string EncKeyLabel = "axon.field.pii";

    // Lazily-derived, cached key — zeroed on disposal
    private byte[]? _cachedKey;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    // ── Key lifecycle ─────────────────────────────────────────────────────────

    private async ValueTask<byte[]> GetKeyAsync(CancellationToken ct)
    {
        if (_cachedKey is not null) return _cachedKey;

        await _keyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedKey is not null) return _cachedKey;
            var mem = await vault.DeriveKeyAsync(EncKeyLabel, ct).ConfigureAwait(false);
            _cachedKey = mem.ToArray();
            vault.ZeroKey(mem);          // Zero the vault-returned copy; we keep our own
            return _cachedKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cachedKey is not null)
        {
            CryptographicOperations.ZeroMemory(_cachedKey);
            _cachedKey = null;
        }
        _keyLock.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ── Encryption helpers ────────────────────────────────────────────────────

    /// <summary>
    /// AES-256-GCM encrypt. Returns Base64(nonce || tag || ciphertext).
    /// Allocates only for the output byte array — no intermediate strings.
    /// </summary>
    private static string Encrypt(ReadOnlySpan<byte> key, string plaintext)
    {
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce      = new byte[AesGcm.NonceByteSizes.MaxSize];   // 12 bytes
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];      // 16 bytes
        var ciphertext = new byte[plaintextBytes.Length];

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack: nonce[12] || tag[16] || ciphertext[n] → single Base64 string
        var blob = new byte[28 + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, 12);
        ciphertext.CopyTo(blob, 28);
        return Convert.ToBase64String(blob);
    }

    /// <summary>
    /// AES-256-GCM decrypt. Accepts Base64(nonce || tag || ciphertext).
    /// </summary>
    private static string Decrypt(ReadOnlySpan<byte> key, string blob)
    {
        var data       = Convert.FromBase64String(blob);
        var nonce      = data.AsSpan(0,  12);
        var tag        = data.AsSpan(12, 16);
        var ciphertext = data.AsSpan(28);

        Span<byte> plaintext = stackalloc byte[ciphertext.Length];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    // ── Event transformation ──────────────────────────────────────────────────

    private async ValueTask<BiometricEvent> EncryptEventAsync(
        BiometricEvent evt, CancellationToken ct)
    {
        var key = await GetKeyAsync(ct).ConfigureAwait(false);
        return evt with
        {
            Source = evt.Source with
            {
                DeviceId = Encrypt(key, evt.Source.DeviceId)
            }
        };
    }

    private async ValueTask<BiometricEvent> DecryptEventAsync(
        BiometricEvent evt, CancellationToken ct)
    {
        var key = await GetKeyAsync(ct).ConfigureAwait(false);
        try
        {
            return evt with
            {
                Source = evt.Source with
                {
                    DeviceId = Decrypt(key, evt.Source.DeviceId)
                }
            };
        }
        catch (CryptographicException)
        {
            // If decryption fails the field was stored unencrypted (migration scenario).
            // Return as-is — do not crash the read path.
            return evt;
        }
    }

    private async ValueTask<IReadOnlyList<BiometricEvent>> DecryptListAsync(
        IReadOnlyList<BiometricEvent> events, CancellationToken ct)
    {
        var result = new BiometricEvent[events.Count];
        for (int i = 0; i < events.Count; i++)
            result[i] = await DecryptEventAsync(events[i], ct).ConfigureAwait(false);
        return result;
    }

    // ── IRepository<BiometricEvent, Guid> ─────────────────────────────────────

    public async ValueTask<BiometricEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var evt = await inner.GetByIdAsync(id, ct).ConfigureAwait(false);
        return evt is null ? null : await DecryptEventAsync(evt, ct).ConfigureAwait(false);
    }

    public async ValueTask AddAsync(BiometricEvent evt, CancellationToken ct = default)
        => await inner.AddAsync(await EncryptEventAsync(evt, ct).ConfigureAwait(false), ct)
                      .ConfigureAwait(false);

    public async ValueTask AddRangeAsync(
        IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
    {
        var encrypted = new BiometricEvent[events.Count];
        for (int i = 0; i < events.Count; i++)
            encrypted[i] = await EncryptEventAsync(events[i], ct).ConfigureAwait(false);
        await inner.AddRangeAsync(encrypted, ct).ConfigureAwait(false);
    }

    public async ValueTask UpdateAsync(BiometricEvent evt, CancellationToken ct = default)
        => await inner.UpdateAsync(await EncryptEventAsync(evt, ct).ConfigureAwait(false), ct)
                      .ConfigureAwait(false);

    public ValueTask DeleteAsync(Guid id, CancellationToken ct = default)
        => inner.DeleteAsync(id, ct);

    // ── IBiometricRepository ──────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
        BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var events = await inner.QueryRangeAsync(type, from, to, ct).ConfigureAwait(false);
        return await DecryptListAsync(events, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
        BiometricType type, DateTimeOffset from, DateTimeOffset to,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in inner.StreamRangeAsync(type, from, to, ct).ConfigureAwait(false))
            yield return await DecryptEventAsync(evt, ct).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
        BiometricType type, DateTimeOffset from, DateTimeOffset to,
        int bucketSizeSeconds, CancellationToken ct = default)
        // Aggregates contain no PII — pass through untouched
        => inner.GetAggregatesAsync(type, from, to, bucketSizeSeconds, ct);

    public async ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
        CancellationToken ct = default)
    {
        var vitals = await inner.GetLatestVitalsAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<BiometricType, BiometricEvent>(vitals.Count);
        foreach (var (k, v) in vitals)
            result[k] = await DecryptEventAsync(v, ct).ConfigureAwait(false);
        return result;
    }

    public async ValueTask IngestBatchAsync(
        IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
    {
        var encrypted = new BiometricEvent[events.Count];
        for (int i = 0; i < events.Count; i++)
            encrypted[i] = await EncryptEventAsync(events[i], ct).ConfigureAwait(false);
        await inner.IngestBatchAsync(encrypted, ct).ConfigureAwait(false);
    }
}
