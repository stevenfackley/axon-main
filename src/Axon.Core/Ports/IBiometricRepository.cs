using Axon.Core.Domain;

namespace Axon.Core.Ports;

/// <summary>
/// Biometric-specific query port layered on top of <see cref="IRepository{TEntity,TId}"/>.
/// Exposes time-series slicing, LTTB-ready bulk reads, and per-type filtering.
///
/// Performance contract:
///   • Any range query spanning > 24 hours MUST route through the LTTB
///     downsampling pipeline before results reach the UI layer.
///   • Implementations use WAL-mode SQLite cursors; never load unbounded
///     result sets into managed memory.
/// </summary>
public interface IBiometricRepository : IRepository<BiometricEvent, Guid>
{
    /// <summary>
    /// Returns events of <paramref name="type"/> in the half-open UTC interval
    /// [<paramref name="from"/>, <paramref name="to"/>).
    /// Results are ordered by <see cref="BiometricEvent.Timestamp"/> ascending.
    /// </summary>
    ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
        BiometricType  type,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Streams events via an <see cref="IAsyncEnumerable{T}"/> to avoid materialising
    /// large result sets. Used by the LTTB downsampling service for spans > 24 hours.
    /// </summary>
    IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
        BiometricType  type,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Returns pre-aggregated (min/max/avg) buckets for macro-level LoD rendering.
    /// Bucket size is expressed in seconds.
    /// </summary>
    ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
        BiometricType  type,
        DateTimeOffset from,
        DateTimeOffset to,
        int            bucketSizeSeconds,
        CancellationToken ct = default);

    /// <summary>Latest single reading for each <see cref="BiometricType"/>.</summary>
    ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Atomically writes <paramref name="events"/> AND their corresponding
    /// <see cref="SyncOutboxEntry"/> records in a single EF Core transaction.
    /// This is the ONLY sanctioned write path — do not bypass with AddRangeAsync.
    /// </summary>
    ValueTask IngestBatchAsync(
        IReadOnlyList<BiometricEvent> events,
        CancellationToken ct = default);
}

/// <summary>Pre-computed aggregate bucket for macro LoD chart rendering.</summary>
/// <param name="BucketStart">UTC start of the time bucket.</param>
/// <param name="Min">Minimum value in bucket.</param>
/// <param name="Max">Maximum value in bucket.</param>
/// <param name="Avg">Mean value in bucket.</param>
/// <param name="SampleCount">Number of raw events aggregated.</param>
public readonly record struct AggregateBucket(
    DateTimeOffset BucketStart,
    double         Min,
    double         Max,
    double         Avg,
    int            SampleCount);
