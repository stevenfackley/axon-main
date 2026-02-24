using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Persistence.Entities;
using Axon.Infrastructure.Persistence.Mappers;
using Microsoft.EntityFrameworkCore;

namespace Axon.Infrastructure.Security;

/// <summary>
/// HIPAA audit logger that persists <see cref="AuditLogEntry"/> records to the
/// immutable <c>AuditLog</c> SQLite table.
///
/// Guarantees:
///   • <see cref="LogAsync"/> always completes its own <c>SaveChangesAsync</c>
///     call — writes are durable before the method returns to the decorator.
///   • Caller identity is stored as a SHA-256 hash (hex) — never the raw
///     username or user ID, satisfying the GDPR minimisation principle.
/// </summary>
public sealed class DbAuditLogger(AxonDbContext db) : IAuditLogger
{
    public async ValueTask LogAsync(
        AuditOperation  operation,
        string          repositoryName,
        string          callerIdentity,
        string?         affectedEntityId,
        string          summary,
        CancellationToken ct = default)
    {
        var entity = new AuditLogEntity
        {
            Id               = Guid.NewGuid(),
            OccurredAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Operation        = (byte)operation,
            RepositoryName   = repositoryName,
            CallerIdentity   = HashIdentity(callerIdentity),
            AffectedEntityId = affectedEntityId,
            Summary          = summary,
        };

        db.AuditLog.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AuditLogEntry>> GetTrailAsync(
        string entityId, CancellationToken ct = default)
    {
        var entities = await db.AuditLog
            .AsNoTracking()
            .Where(e => e.AffectedEntityId == entityId)
            .OrderByDescending(e => e.OccurredAtUnixMs)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new AuditLogEntry[entities.Count];
        for (int i = 0; i < entities.Count; i++)
            result[i] = AuditLogMapper.ToDomain(entities[i]);
        return result;
    }

    /// <summary>
    /// SHA-256 hashes the caller identity to prevent PII exposure in the audit log.
    /// The same identity always produces the same hash (deterministic), enabling
    /// correlation without storing the raw value.
    /// </summary>
    private static string HashIdentity(string identity)
    {
        Span<byte> hash  = stackalloc byte[32];
        Span<byte> input = stackalloc byte[System.Text.Encoding.UTF8.GetMaxByteCount(identity.Length)];
        int written = System.Text.Encoding.UTF8.GetBytes(identity, input);
        System.Security.Cryptography.SHA256.HashData(input[..written], hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
