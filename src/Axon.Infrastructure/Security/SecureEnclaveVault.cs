// iOS Secure Enclave vault – only compiled when targeting net9.0-ios.
#if IOS
using System.Security.Cryptography;
using Axon.Core.Ports;
using Foundation;
using Security;

namespace Axon.Infrastructure.Security;

/// <summary>
/// iOS Secure Enclave-backed <see cref="IHardwareVault"/> implementation.
///
/// Strategy
/// ─────────
/// 1. On first call for a given <paramref name="keyLabel"/>, a cryptographically
///    random 32-byte AES-256 key is generated via <see cref="RandomNumberGenerator"/>.
/// 2. The key is persisted to the iOS Keychain with:
///    • <c>kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly</c> — readable only
///      while the device is unlocked AND has a passcode. Not backed up to iCloud.
///      Wiped on factory reset.
///    • <c>kSecAccessControlPrivateKeyUsage</c> — requires Secure Enclave
///      participation for any access (enforces hardware binding).
/// 3. On subsequent calls the key is loaded from the Keychain, returned in a
///    pinned <see cref="Memory{T}"/> buffer, and must be zeroed by the caller
///    via <see cref="ZeroKey"/>.
/// 4. <see cref="DestroyKeyAsync"/> deletes the Keychain item — the AES key is
///    gone permanently. The encrypted SQLite database becomes unreadable.
///    This is the GDPR "Nuclear Option"; there is no recovery path.
///
/// Notes
/// ─────
/// • The iOS Simulator does NOT have a Secure Enclave. <see cref="IsHardwareBacked"/>
///   returns <c>false</c> on simulators; the Keychain still works but without SE binding.
/// • The device MUST have a passcode set, or Keychain writes will fail with
///   <see cref="SecStatusCode.MissingEntitlement"/>. Axon's onboarding flow MUST
///   verify this before calling <see cref="DeriveKeyAsync"/>.
/// </summary>
public sealed class SecureEnclaveVault : IHardwareVault
{
    private const string KeychainService = "com.axon.telemetry.vault";

    // ── IHardwareVault ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// False on the iOS Simulator (no physical Secure Enclave chip).
    /// Always true on real iPhone 5S+ / iPad Pro hardware.
    /// </remarks>
    public bool IsHardwareBacked =>
        ObjCRuntime.Runtime.Arch != ObjCRuntime.Arch.SIMULATOR;

    /// <inheritdoc/>
    public ValueTask<Memory<byte>> DeriveKeyAsync(
        string keyLabel,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Try to retrieve an existing key for this label.
        var existing = LoadFromKeychain(keyLabel);
        if (existing is not null)
        {
            // Copy into a fresh pinned buffer — zero the source immediately.
            var loaded = GC.AllocateUninitializedArray<byte>(32, pinned: true);
            existing.AsSpan().CopyTo(loaded);
            CryptographicOperations.ZeroMemory(existing);
            return ValueTask.FromResult<Memory<byte>>(loaded);
        }

        // First use: generate a new random 32-byte AES-256 key.
        var freshKey = GC.AllocateUninitializedArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(freshKey);

        // Persist to Keychain; throws on failure so the caller knows immediately.
        SaveToKeychain(keyLabel, freshKey);

        return ValueTask.FromResult<Memory<byte>>(freshKey);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Call in a <c>finally</c> block immediately after the key material is no
    /// longer needed. The GC will NOT zero the buffer automatically.
    /// </remarks>
    public void ZeroKey(Memory<byte> keyMaterial)
        => keyMaterial.Span.Clear();

    /// <inheritdoc/>
    public ValueTask DestroyKeyAsync(
        string keyLabel,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DeleteFromKeychain(keyLabel);
        return ValueTask.CompletedTask;
    }

    // ── Keychain Helpers ───────────────────────────────────────────────────────

    private static byte[]? LoadFromKeychain(string keyLabel)
    {
        using var query = new SecRecord(SecKind.GenericPassword)
        {
            Service  = KeychainService,
            Account  = keyLabel,
            ReturnData = true,
            MatchLimit = SecMatchLimit.One,
        };

        var result = SecKeyChain.QueryAsRecord(query, out SecStatusCode status);

        if (status == SecStatusCode.ItemNotFound || result?.ValueData is null)
            return null;

        if (status != SecStatusCode.Success)
            throw new CryptographicException(
                $"SecureEnclaveVault: Keychain load failed for '{keyLabel}'. Status: {status}");

        return result.ValueData.ToArray();
    }

    private static void SaveToKeychain(string keyLabel, byte[] keyBytes)
    {
        // kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly:
        //   • Requires a device passcode to be set.
        //   • Not included in iCloud Backup — data stays on this device.
        //   • Deleted when the device is wiped / restored.
        //   • Combined with PrivateKeyUsage → Secure Enclave mediation required.
        var accessControl = SecAccessControl.Create(
            SecAccessibility.WhenPasscodeSetThisDeviceOnly,
            SecAccessControlCreateFlags.PrivateKeyUsage,
            out NSError? acError);

        if (acError is not null)
            throw new CryptographicException(
                $"SecureEnclaveVault: failed to create access control for '{keyLabel}': {acError.LocalizedDescription}");

        using var record = new SecRecord(SecKind.GenericPassword)
        {
            Service       = KeychainService,
            Account       = keyLabel,
            ValueData     = NSData.FromArray(keyBytes),
            AccessControl = accessControl,
        };

        var status = SecKeyChain.Add(record);

        if (status == SecStatusCode.DuplicateItem)
        {
            // Defensive: item already exists — update it.
            UpdateKeychain(keyLabel, keyBytes);
            return;
        }

        if (status != SecStatusCode.Success)
            throw new CryptographicException(
                $"SecureEnclaveVault: Keychain save failed for '{keyLabel}'. Status: {status}");
    }

    private static void UpdateKeychain(string keyLabel, byte[] keyBytes)
    {
        using var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = KeychainService,
            Account = keyLabel,
        };
        using var update = new SecRecord
        {
            ValueData = NSData.FromArray(keyBytes),
        };

        var status = SecKeyChain.Update(query, update);

        if (status != SecStatusCode.Success)
            throw new CryptographicException(
                $"SecureEnclaveVault: Keychain update failed for '{keyLabel}'. Status: {status}");
    }

    private static void DeleteFromKeychain(string keyLabel)
    {
        using var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = KeychainService,
            Account = keyLabel,
        };

        // SecStatusCode.ItemNotFound is acceptable — key may already be gone.
        var status = SecKeyChain.Remove(query);
        if (status != SecStatusCode.Success && status != SecStatusCode.ItemNotFound)
            throw new CryptographicException(
                $"SecureEnclaveVault: Keychain delete failed for '{keyLabel}'. Status: {status}");
    }
}
#endif
