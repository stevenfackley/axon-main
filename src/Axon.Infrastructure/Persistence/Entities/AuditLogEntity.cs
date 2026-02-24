namespace Axon.Infrastructure.Persistence.Entities;

/// <summary>
/// EF Core entity for the immutable HIPAA <c>AuditLog</c> table.
/// This table must never have DELETE permissions granted in the SQLite
/// connection security policy.
/// </summary>
internal sealed class AuditLogEntity
{
    public Guid    Id                { get; set; }
    public long    OccurredAtUnixMs  { get; set; }
    public byte    Operation         { get; set; }
    public string  RepositoryName   { get; set; }  = string.Empty;
    public string  CallerIdentity   { get; set; }  = string.Empty;  // SHA-256 hash of user identity
    public string? AffectedEntityId { get; set; }
    public string  Summary          { get; set; }  = string.Empty;
}
