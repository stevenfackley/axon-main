using Axon.Core.Domain;
using Axon.Infrastructure.Persistence.Entities;

namespace Axon.Infrastructure.Persistence.Mappers;

/// <summary>
/// Bidirectional mapper for <see cref="AuditLogEntry"/> ↔ <see cref="AuditLogEntity"/>.
/// </summary>
internal static class AuditLogMapper
{
    internal static AuditLogEntity ToEntity(AuditLogEntry domain) => new()
    {
        Id               = domain.Id,
        OccurredAtUnixMs = domain.OccurredAt.ToUnixTimeMilliseconds(),
        Operation        = (byte)domain.Operation,
        RepositoryName   = domain.RepositoryName,
        CallerIdentity   = domain.CallerIdentity,
        AffectedEntityId = domain.AffectedEntityId,
        Summary          = domain.Summary,
    };

    internal static AuditLogEntry ToDomain(AuditLogEntity entity) => new(
        Id:               entity.Id,
        OccurredAt:       DateTimeOffset.FromUnixTimeMilliseconds(entity.OccurredAtUnixMs),
        Operation:        (AuditOperation)entity.Operation,
        RepositoryName:   entity.RepositoryName,
        CallerIdentity:   entity.CallerIdentity,
        AffectedEntityId: entity.AffectedEntityId,
        Summary:          entity.Summary);
}
