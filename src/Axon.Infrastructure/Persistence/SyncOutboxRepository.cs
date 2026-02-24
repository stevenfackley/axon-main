using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence.Mappers;
using Microsoft.EntityFrameworkCore;

namespace Axon.Infrastructure.Persistence;

/// <summary>
/// Concrete EF Core adapter for <see cref="ISyncOutboxRepository"/>.
///
/// All mutation methods (<see cref="MarkProcessedAsync"/>, <see cref="MarkFailedAsync"/>)
/// use <c>ExecuteUpdateAsync</c> — a single-round-trip UPDATE with no entity tracking,
/// fulfilling the "no I/O inside a DB transaction" guardrail.
/// </summary>
public sealed class SyncOutboxRepository(AxonDbContext db) : ISyncOutboxRepository
{
    // ── IRepository<SyncOutboxEntry, Guid> ───────────────────────────────────

    public async ValueTask<SyncOutboxEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.SyncOutbox
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct)
            .ConfigureAwait(false);
        return entity is null ? null : SyncOutboxMapper.ToDomain(entity);
    }

    public async ValueTask AddAsync(SyncOutboxEntry entry, CancellationToken ct = default)
    {
        db.SyncOutbox.Add(SyncOutboxMapper.ToEntity(entry));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask AddRangeAsync(
        IReadOnlyList<SyncOutboxEntry> entries, CancellationToken ct = default)
    {
        foreach (var e in entries)
            db.SyncOutbox.Add(SyncOutboxMapper.ToEntity(e));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask UpdateAsync(SyncOutboxEntry entry, CancellationToken ct = default)
    {
        db.SyncOutbox.Update(SyncOutboxMapper.ToEntity(entry));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await db.SyncOutbox
            .Where(e => e.Id == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    // ── ISyncOutboxRepository ─────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<SyncOutboxEntry>> GetPendingAsync(
        int batchSize = 100, CancellationToken ct = default)
    {
        var entities = await db.SyncOutbox
            .AsNoTracking()
            .Where(e => e.ProcessedAtUnixMs == null)
            .OrderBy(e => e.CreatedAtUnixMs)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new SyncOutboxEntry[entities.Count];
        for (int i = 0; i < entities.Count; i++)
            result[i] = SyncOutboxMapper.ToDomain(entities[i]);
        return result;
    }

    public async ValueTask MarkProcessedAsync(Guid entryId, CancellationToken ct = default)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SyncOutbox
            .Where(e => e.Id == entryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.ProcessedAtUnixMs, nowMs), ct)
            .ConfigureAwait(false);
    }

    public async ValueTask MarkFailedAsync(Guid entryId, string error, CancellationToken ct = default)
    {
        await db.SyncOutbox
            .Where(e => e.Id == entryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.RetryCount, e => e.RetryCount + 1)
                .SetProperty(e => e.LastError,  error), ct)
            .ConfigureAwait(false);
    }

    public async ValueTask PurgeOldAsync(int retentionDays = 30, CancellationToken ct = default)
    {
        long cutoffMs = DateTimeOffset.UtcNow
            .AddDays(-retentionDays)
            .ToUnixTimeMilliseconds();

        await db.SyncOutbox
            .Where(e => e.ProcessedAtUnixMs != null && e.ProcessedAtUnixMs < cutoffMs)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
