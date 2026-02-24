namespace Axon.Core.Ports;

/// <summary>
/// Zero-knowledge key-derivation port.
///
/// Platform implementations:
///   • Windows  → TPM 2.0 via <c>Windows.Security.Cryptography.DataProtection</c>
///   • iOS      → Secure Enclave via <c>Security.framework</c> (kSecAttrTokenIDSecureEnclave)
///   • Android  → StrongBox Keymaster via <c>Android.Security.Keystore</c>
///
/// Contract:
///   • No raw key material is ever written to managed memory as a <see cref="string"/>.
///     Callers receive key bytes in a <see cref="Memory{T}"/> that is zeroed
///     after use by calling <see cref="ZeroKey"/>.
///   • <see cref="DeriveKeyAsync"/> is deterministic for a given <paramref name="keyLabel"/>;
///     repeated calls return the same key bytes (hardware sealing ensures this).
/// </summary>
public interface IHardwareVault
{
    /// <summary>
    /// Derives (or retrieves) a hardware-backed AES-256 key identified by
    /// <paramref name="keyLabel"/>. The caller MUST call <see cref="ZeroKey"/>
    /// when the key material is no longer needed.
    /// </summary>
    /// <param name="keyLabel">Logical key name (e.g. "axon.db.master", "axon.sync.transport").</param>
    /// <param name="ct">Cancellation for hardware I/O timeout.</param>
    /// <returns>32-byte AES-256 key material in a pinned <see cref="Memory{T}"/> segment.</returns>
    ValueTask<Memory<byte>> DeriveKeyAsync(string keyLabel, CancellationToken ct = default);

    /// <summary>
    /// Immediately overwrites the key buffer with zeros and releases the
    /// pinned GC handle. Must be called in a <c>finally</c> block.
    /// </summary>
    void ZeroKey(Memory<byte> keyMaterial);

    /// <summary>
    /// Executes the GDPR "Nuclear Option": deletes the hardware-bound key
    /// identified by <paramref name="keyLabel"/> from the secure enclave/TPM.
    /// After this call, the associated encrypted database is permanently
    /// unreadable — there is no recovery path.
    /// </summary>
    ValueTask DestroyKeyAsync(string keyLabel, CancellationToken ct = default);

    /// <summary>
    /// Returns whether the current runtime platform has a genuine hardware
    /// security module. False on simulators / emulators.
    /// </summary>
    bool IsHardwareBacked { get; }
}
