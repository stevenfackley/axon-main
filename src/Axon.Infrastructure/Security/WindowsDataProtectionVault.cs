using System.Runtime.Versioning;
using System.Security.Cryptography;
using Axon.Core.Ports;

namespace Axon.Infrastructure.Security;

/// <summary>
/// Windows DPAPI-backed implementation of <see cref="IHardwareVault"/>.
///
/// Keys are generated once as random 32-byte values, protected with
/// <see cref="ProtectedData"/> (CurrentUser scope) and written to disk
/// as <c>{dir}/{sanitizedLabel}.key</c>.  Subsequent calls unprotect and
/// return the same bytes, making the derivation stable across process restarts.
///
/// Callers MUST call <see cref="ZeroKey"/> in a <c>finally</c> block to wipe
/// the in-memory copy.
/// </summary>
public sealed class WindowsDataProtectionVault : IHardwareVault
{
    private readonly string _keyDirectory;

    /// <summary>
    /// Initialises a new vault that stores protected key blobs in
    /// <paramref name="keyDirectory"/>.  The directory must already exist.
    /// </summary>
    /// <param name="keyDirectory">Directory path for <c>*.key</c> blob files.</param>
    public WindowsDataProtectionVault(string keyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyDirectory);
        _keyDirectory = keyDirectory;
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see langword="true"/> on Windows; DPAPI is an OS-level credential store.</remarks>
    public bool IsHardwareBacked => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    /// <remarks>
    /// On first call for a given <paramref name="keyLabel"/> a random 32-byte key
    /// is generated, DPAPI-protected and written to disk.  On subsequent calls the
    /// blob is read back and unprotected.  The returned <see cref="Memory{T}"/> is
    /// backed by a pinned GC array so the caller can zero it with <see cref="ZeroKey"/>.
    /// Throws <see cref="PlatformNotSupportedException"/> on non-Windows platforms.
    /// </remarks>
    public ValueTask<Memory<byte>> DeriveKeyAsync(
        string keyLabel, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyLabel);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                $"{nameof(WindowsDataProtectionVault)} requires Windows (DPAPI).");

        var blobPath = BlobPath(keyLabel);
        byte[] rawKey;

        if (File.Exists(blobPath))
        {
            // Existing key: read protected blob and unprotect.
            var blob = File.ReadAllBytes(blobPath);
            rawKey = UnprotectWindows(blob);
        }
        else
        {
            // New key: generate, protect, persist.
            rawKey = RandomNumberGenerator.GetBytes(32);
            var blob = ProtectWindows(rawKey);
            File.WriteAllBytes(blobPath, blob);
        }

        // Return a pinned copy so the caller can zero it.
        var buffer = GC.AllocateUninitializedArray<byte>(32, pinned: true);
        rawKey.AsSpan().CopyTo(buffer);
        CryptographicOperations.ZeroMemory(rawKey);   // wipe the intermediate array

        return ValueTask.FromResult<Memory<byte>>(buffer);
    }

    /// <inheritdoc/>
    public void ZeroKey(Memory<byte> keyMaterial) => keyMaterial.Span.Clear();

    /// <inheritdoc/>
    /// <remarks>
    /// Overwrites the blob file with zeros before deleting it so the key
    /// material is not recoverable from the file-system journal.
    /// </remarks>
    public ValueTask DestroyKeyAsync(string keyLabel, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyLabel);

        var blobPath = BlobPath(keyLabel);
        if (File.Exists(blobPath))
        {
            // Secure-erase: overwrite with zeros, then delete.
            var length = new FileInfo(blobPath).Length;
            if (length > 0)
            {
                using var fs = new FileStream(
                    blobPath, FileMode.Open, FileAccess.Write, FileShare.None);
                var zeros = new byte[length];
                fs.Write(zeros, 0, zeros.Length);
                fs.Flush();
            }

            File.Delete(blobPath);
        }

        return ValueTask.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Converts a logical key label to a safe file name by replacing
    /// characters that are invalid in file names with underscores.
    /// </summary>
    private string BlobPath(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Create(label.Length, (label, invalid), static (span, state) =>
        {
            var (src, inv) = state;
            for (var i = 0; i < src.Length; i++)
                span[i] = Array.IndexOf(inv, src[i]) >= 0 ? '_' : src[i];
        });

        return Path.Combine(_keyDirectory, $"{safe}.key");
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data) =>
        ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] blob) =>
        ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
}
