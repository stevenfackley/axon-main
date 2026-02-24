namespace Axon.Core.Domain;

/// <summary>
/// Immutable, append-only HIPAA audit record written by <c>AuditLoggingDecorator</c>.
/// This table must never be truncated or mutated post-write.
/// </summary>
public sealed record AuditLogEntry(
    Guid            Id,
    DateTimeOffset  OccurredAt,
    AuditOperation  Operation,
    string          RepositoryName,
    string          CallerIdentity,   // Hashed — never raw user name
    string?         AffectedEntityId,
    string          Summary)           // Non-PII human-readable description
{
    /// <summary>PII Shield.</summary>
    public override string ToString() =>
        $"AuditLogEntry {{ Id={Id}, Op={Operation}, At={OccurredAt:O} }}";
}

/// <summary>Discriminated union of auditable operations.</summary>
public enum AuditOperation : byte
{
    Read    = 0,
    Write   = 1,
    Delete  = 2,
    Sync    = 3,
    KeyAccess = 4
}
