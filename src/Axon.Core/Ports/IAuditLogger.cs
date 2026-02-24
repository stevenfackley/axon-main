using Axon.Core.Domain;

namespace Axon.Core.Ports;

/// <summary>
/// HIPAA-compliant audit logging port.
/// All writes are append-only and must be persisted before the originating
/// repository call returns to the caller.
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Records an access event. The implementation MUST flush to durable
    /// storage synchronously relative to the DB transaction — not fire-and-forget.
    /// </summary>
    ValueTask LogAsync(
        AuditOperation  operation,
        string          repositoryName,
        string          callerIdentity,
        string?         affectedEntityId,
        string          summary,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the audit trail for a specific entity ordered by
    /// <see cref="AuditLogEntry.OccurredAt"/> descending.
    /// </summary>
    ValueTask<IReadOnlyList<AuditLogEntry>> GetTrailAsync(
        string entityId,
        CancellationToken ct = default);
}
