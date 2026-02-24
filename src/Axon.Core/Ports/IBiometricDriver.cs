using Axon.Core.Domain;

namespace Axon.Core.Ports;

/// <summary>
/// Primary port for all biometric data sources (Whoop, Garmin, Oura, Apple Health,
/// Android Health Connect, CSV import, etc.).
///
/// Implementation requirements for any adapter:
///   1. Map ALL vendor-specific fields to <see cref="BiometricEvent"/> using
///      the vendor's dedicated NormalizationMapper before returning data.
///   2. Use <see cref="System.Buffers.ArrayPool{T}"/> for intermediate parsing
///      buffers; never allocate new arrays in the ingestion hot path.
///   3. NormalizationMapper must have 100% unit-test coverage (DoD).
///   4. Document the driver in DRIVER_REGISTRY.md.
/// </summary>
public interface IBiometricDriver
{
    /// <summary>Unique identifier matching the vendor string in <see cref="SourceMetadata"/>.</summary>
    string DriverId { get; }

    /// <summary>Human-readable vendor name for UI display (e.g. "Whoop 4.0").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Returns true when the driver can emit data without outbound network access.
    /// All drivers must function offline; API-backed drivers return false here and
    /// degrade gracefully when <c>AirGapMode</c> is active.
    /// </summary>
    bool SupportsOffline { get; }

    /// <summary>
    /// Tests whether this driver can reach its data source (device paired, API
    /// reachable, file readable, etc.).
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs one-time authorisation handshake (OAuth, BLE pairing, file-open,
    /// etc.). Must be idempotent — safe to call repeatedly.
    /// </summary>
    ValueTask AuthoriseAsync(CancellationToken ct = default);

    /// <summary>
    /// Pulls all events newer than <paramref name="since"/> from the data source.
    /// Results arrive as a lazy <see cref="IAsyncEnumerable{T}"/> so the ingestion
    /// pipeline can process events via <c>System.Threading.Channels</c> without
    /// buffering the full payload in memory.
    /// </summary>
    IAsyncEnumerable<BiometricEvent> FetchSinceAsync(
        DateTimeOffset    since,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes to a real-time push stream (BLE GATT notify, WebSocket, etc.).
    /// Implementations that do not support streaming should throw
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    IAsyncEnumerable<BiometricEvent> StreamLiveAsync(CancellationToken ct = default);
}
