using System.Security.Cryptography;
using System.Text;
using Axon.Core.Domain;

namespace Axon.Infrastructure.Drivers;

/// <summary>
/// Shared utilities used by all <see cref="Axon.Core.Ports.IBiometricDriver"/> adapters.
/// All methods are static and pure — no side effects, no I/O.
/// </summary>
internal static class DriverUtilities
{
    // Axon UUID v5 namespace (stable across all driver builds)
    private static readonly Guid AxonNamespace =
        new("6ba7b810-9dad-11d1-80b4-00c04fd430c8"); // RFC 4122 URL namespace

    /// <summary>
    /// Generates a deterministic <see cref="Guid"/> from (vendor, deviceId, timestamp, type)
    /// using UUID v5 (SHA-1) so that re-ingesting the same raw sample always produces the
    /// same <c>Id</c>, enabling idempotent upserts in the persistence layer.
    /// </summary>
    public static Guid DeterministicId(
        string          vendor,
        string          deviceId,
        DateTimeOffset  timestamp,
        BiometricType   type)
    {
        var input = $"{vendor}|{deviceId}|{timestamp:O}|{(byte)type}";
        return GuidV5.Create(AxonNamespace, input);
    }

    /// <summary>
    /// Builds a <see cref="SourceMetadata"/> record for a vendor driver.
    /// </summary>
    public static SourceMetadata BuildSource(
        string  vendor,
        string  deviceId,
        float   confidenceScore,
        string? firmwareVersion = null) =>
        new(
            DeviceId:           deviceId,
            Vendor:             vendor,
            FirmwareVersion:    firmwareVersion,
            ConfidenceScore:    confidenceScore,
            IngestionTimestamp: DateTimeOffset.UtcNow);
}

/// <summary>
/// Minimal UUID v5 (SHA-1 hash) generator — avoids a NuGet dependency for a single utility.
/// Shared across all driver NormalizationMappers.
/// </summary>
internal static class GuidV5
{
    public static Guid Create(Guid namespaceId, string name)
    {
        var nsBytes = namespaceId.ToByteArray();
        // Convert namespace GUID bytes from .NET little-endian to RFC 4122 big-endian
        SwapBytes(nsBytes, 0, 3);
        SwapBytes(nsBytes, 1, 2);
        SwapBytes(nsBytes, 4, 5);
        SwapBytes(nsBytes, 6, 7);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var combined  = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes,   0, combined, 0,             nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, combined, nsBytes.Length, nameBytes.Length);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(combined, hash);

        // Set version (5) and variant bits per RFC 4122 §4.3
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var guidBytes = hash[..16].ToArray();
        // Convert back to .NET little-endian for Guid constructor
        SwapBytes(guidBytes, 0, 3);
        SwapBytes(guidBytes, 1, 2);
        SwapBytes(guidBytes, 4, 5);
        SwapBytes(guidBytes, 6, 7);

        return new Guid(guidBytes);
    }

    private static void SwapBytes(byte[] b, int i, int j) => (b[i], b[j]) = (b[j], b[i]);
}
