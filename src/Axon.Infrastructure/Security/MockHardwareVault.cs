using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Axon.Core.Ports;

namespace Axon.Infrastructure.Security;

/// <summary>
/// Development/test substitute for a real hardware-backed key vault.
///
/// ⚠ WARNING: This implementation is NOT secure for production use.
///   It derives a key from a seeded RNG so tests are deterministic across runs
///   (same label → same key within a process lifetime). It does NOT use any
///   TPM, Secure Enclave, or HSM. Replace with a platform adapter before shipping.
///
/// Platform adapters to implement:
///   • Windows  → <c>WindowsTpmVault</c>  using DPAPI/TPM2.0
///   • iOS      → <c>SecureEnclaveVault</c> using kSecAttrTokenIDSecureEnclave
///   • Android  → <c>AndroidKeystoreVault</c> using StrongBox Keymaster
/// </summary>
public sealed class MockHardwareVault : IHardwareVault
{
    // In-process key store: label → 32-byte key (never written to disk)
    private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <inheritdoc/>
    public bool IsHardwareBacked => false;

    /// <inheritdoc/>
    public ValueTask<Memory<byte>> DeriveKeyAsync(
        string keyLabel, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_store.TryGetValue(keyLabel, out var existing))
            {
                // Generate a deterministic 32-byte key from the label hash
                // so tests can recreate the same "vault" across instances.
                existing = new byte[32];
                using var sha = SHA256.Create();
                var labelBytes = System.Text.Encoding.UTF8.GetBytes(keyLabel);
                sha.TryComputeHash(labelBytes, existing, out _);
                _store[keyLabel] = existing;
            }

            // Return a COPY in a pinned Memory<byte> so the caller can zero it
            var buffer = GC.AllocateUninitializedArray<byte>(32, pinned: true);
            existing.AsSpan().CopyTo(buffer);
            return ValueTask.FromResult<Memory<byte>>(buffer);
        }
    }

    /// <inheritdoc/>
    public void ZeroKey(Memory<byte> keyMaterial)
    {
        // Cryptographic erasure: overwrite with zeros before releasing
        keyMaterial.Span.Clear();
    }

    /// <inheritdoc/>
    public ValueTask DestroyKeyAsync(string keyLabel, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_store.TryGetValue(keyLabel, out var key))
            {
                // Zero the stored bytes before removing from dictionary
                CryptographicOperations.ZeroMemory(key);
                _store.Remove(keyLabel);
            }
        }
        return ValueTask.CompletedTask;
    }
}
