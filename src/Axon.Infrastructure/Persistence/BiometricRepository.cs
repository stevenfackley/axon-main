using System.Runtime.CompilerServices;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Core.Serialization;
using Axon.Infrastructure.Persistence.Entities;
using Axon.Infrastructure.Persistence.Mappers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Axon.Infrastructure.Persistence;

/// <summary>
/// Concrete EF Core adapter for <see cref="IBiometricRepository"/>.
///
/// Performance notes:
///   • All query projections use <c>AsNoTracking()</c> — these are immutable
///     domain records; change tracking is wasted overhead.
///   • <see cref="IngestBatchAsync"/> writes BiometricEvents + SyncOutbox
///     entries in a SINGLE <c>SaveChangesAsync</c> call (one transaction).
///   • <see cref="StreamRangeAsync"/> uses EF Core's <c>AsAsyncEnumerable</c>
///     to avoid materialising large result sets on the managed heap.
/// </summary>
public sealed class BiometricRepository(AxonDbContext db) : IBiometricRepository
{
    // ── IRepository<BiometricEvent, Guid> ─────────────────────────────────────

    public async ValueTask<BiometricEvent?> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        var entity = await db.BiometricEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct)
            .ConfigureAwait(false);

        return entity is null ? null : BiometricEventMapper.ToDomain(entity);
    }

    public async ValueTask AddAsync(BiometricEvent evt, CancellationToken ct = default)
    {
        db.BiometricEvents.Add(BiometricEventMapper.ToEntity(evt));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask AddRangeAsync(
        IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
    {
        foreach (var evt in events)
            db.BiometricEvents.Add(BiometricEventMapper.ToEntity(evt));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask UpdateAsync(BiometricEvent evt, CancellationToken ct = default)
    {
        db.BiometricEvents.Update(BiometricEventMapper.ToEntity(evt));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await db.BiometricEvents
            .Where(e => e.Id == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    // ── IBiometricRepository ──────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
        BiometricType  type,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        long fromMs = from.ToUnixTimeMilliseconds();
        long toMs   = to.ToUnixTimeMilliseconds();
        byte typeB  = (byte)type;

        var entities = await db.BiometricEvents
            .AsNoTracking()
            .Where(e => e.BiometricType == typeB
                     && e.TimestampUnixMs >= fromMs
                     && e.TimestampUnixMs <  toMs)
            .OrderBy(e => e.TimestampUnixMs)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Avoid LINQ Select allocation — map manually into a pre-sized array
        var result = new BiometricEvent[entities.Count];
        for (int i = 0; i < entities.Count; i++)
            result[i] = BiometricEventMapper.ToDomain(entities[i]);

        return result;
    }

    public async IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
        BiometricType  type,
        DateTimeOffset from,
        DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long fromMs = from.ToUnixTimeMilliseconds();
        long toMs   = to.ToUnixTimeMilliseconds();
        byte typeB  = (byte)type;

        await foreach (var entity in db.BiometricEvents
            .AsNoTracking()
            .Where(e => e.BiometricType == typeB
                     && e.TimestampUnixMs >= fromMs
                     && e.TimestampUnixMs <  toMs)
            .OrderBy(e => e.TimestampUnixMs)
            .AsAsyncEnumerable()
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            yield return BiometricEventMapper.ToDomain(entity);
        }
    }

    public async ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
        BiometricType  type,
        DateTimeOffset from,
        DateTimeOffset to,
        int            bucketSizeSeconds,
        CancellationToken ct = default)
    {
        long fromMs      = from.ToUnixTimeMilliseconds();
        long toMs        = to.ToUnixTimeMilliseconds();
        long bucketMs    = (long)bucketSizeSeconds * 1000L;
        byte typeB       = (byte)type;

        // SQLite integer arithmetic groups rows into fixed-width time buckets.
        // EF Core translates this to a raw SQL GROUP BY without LINQ overhead.
        var buckets = await db.BiometricEvents
            .AsNoTracking()
            .Where(e => e.BiometricType == typeB
                     && e.TimestampUnixMs >= fromMs
                     && e.TimestampUnixMs <  toMs)
            .GroupBy(e => e.TimestampUnixMs / bucketMs * bucketMs)
            .Select(g => new
            {
                BucketStartMs = g.Key,
                Min           = g.Min(e => e.Value),
                Max           = g.Max(e => e.Value),
                Avg           = g.Average(e => e.Value),
                Count         = g.Count()
            })
            .OrderBy(g => g.BucketStartMs)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new AggregateBucket[buckets.Count];
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            result[i] = new AggregateBucket(
                BucketStart: DateTimeOffset.FromUnixTimeMilliseconds(b.BucketStartMs),
                Min:         b.Min,
                Max:         b.Max,
                Avg:         b.Avg,
                SampleCount: b.Count);
        }
        return result;
    }

    public async ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
        CancellationToken ct = default)
    {
        // Fetch the most-recent event per BiometricType using a subquery.
        // Performed in a single DB round-trip.
        var latest = await db.BiometricEvents
            .AsNoTracking()
            .GroupBy(e => e.BiometricType)
            .Select(g => g.OrderByDescending(e => e.TimestampUnixMs).First())
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var dict = new Dictionary<BiometricType, BiometricEvent>(latest.Count);
        foreach (var entity in latest)
            dict[(BiometricType)entity.BiometricType] = BiometricEventMapper.ToDomain(entity);

        return dict;
    }

    /// <summary>
    /// THE canonical write path. Atomically persists biometric events and their
    /// SyncOutbox entries in one EF Core transaction. Never call AddAsync/AddRangeAsync
    /// for inbound data — use this method exclusively.
    /// </summary>
    public async ValueTask IngestBatchAsync(
        IReadOnlyList<BiometricEvent> events,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid().ToString("N");

        foreach (var evt in events)
        {
            // Persist the event entity
            db.BiometricEvents.Add(BiometricEventMapper.ToEntity(evt));

            // Serialize payload for the outbox (AOT-safe via AxonJsonContext)
            var payload = JsonSerializer.Serialize(evt, AxonJsonContext.Default.BiometricEvent);

            // Write corresponding outbox entry in the SAME unit-of-work
            db.SyncOutbox.Add(new SyncOutboxEntity
            {
                Id               = Guid.NewGuid(),
                BiometricEventId = evt.Id,
                CorrelationId    = evt.CorrelationId ?? correlationId,
                SerializedPayload = payload,   // EncryptionDecorator encrypts this in its layer
                CreatedAtUnixMs  = now.ToUnixTimeMilliseconds(),
                RetryCount       = 0,
            });
        }

        // Single SaveChangesAsync = single SQLite transaction covering both tables
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
