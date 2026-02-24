using Axon.Core.Domain;

namespace Axon.Core.Ports;

/// <summary>
/// Port for the Transactional Outbox table.
///
/// Contract rules (enforced at code-review):
///   1. <see cref="AddAsync"/> is ALWAYS called inside the same EF Core
///      SaveChanges call as the owning <see cref="BiometricEvent"/> write.
///   2. <see cref="MarkProcessedAsync"/> and <see cref="MarkFailedAsync"/> are
///      ALWAYS called OUTSIDE any open DB transaction (no I/O inside tx).
///   3. The relay service polls <see cref="GetPendingAsync"/> on a background
///      thread; it must never block the UI thread.
/// </summary>
public interface ISyncOutboxRepository : IRepository<SyncOutboxEntry, Guid>
{
    /// <summary>
    /// Returns up to <paramref name="batchSize"/> unprocessed entries ordered
    /// by <see cref="SyncOutboxEntry.CreatedAt"/> ascending (oldest-first delivery).
    /// </summary>
    ValueTask<IReadOnlyList<SyncOutboxEntry>> GetPendingAsync(
        int batchSize = 100,
        CancellationToken ct = default);

    /// <summary>Stamps <see cref="SyncOutboxEntry.ProcessedAt"/> with UTC now.</summary>
    ValueTask MarkProcessedAsync(Guid entryId, CancellationToken ct = default);

    /// <summary>Increments retry count and records the failure message.</summary>
    ValueTask MarkFailedAsync(Guid entryId, string error, CancellationToken ct = default);

    /// <summary>
    /// Purges entries processed more than <paramref name="retentionDays"/> ago.
    /// Called by the maintenance service; never called inline.
    /// </summary>
    ValueTask PurgeOldAsync(int retentionDays = 30, CancellationToken ct = default);
}
