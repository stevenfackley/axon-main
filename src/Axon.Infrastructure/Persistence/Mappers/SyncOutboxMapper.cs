using Axon.Core.Domain;
using Axon.Infrastructure.Persistence.Entities;

namespace Axon.Infrastructure.Persistence.Mappers;

/// <summary>
/// Bidirectional mapper for <see cref="SyncOutboxEntry"/> ↔ <see cref="SyncOutboxEntity"/>.
/// </summary>
internal static class SyncOutboxMapper
{
    internal static SyncOutboxEntity ToEntity(SyncOutboxEntry domain) => new()
    {
        Id                = domain.Id,
        BiometricEventId  = domain.BiometricEventId,
        CorrelationId     = domain.CorrelationId,
        SerializedPayload = domain.SerializedPayload,
        CreatedAtUnixMs   = domain.CreatedAt.ToUnixTimeMilliseconds(),
        ProcessedAtUnixMs = domain.ProcessedAt?.ToUnixTimeMilliseconds(),
        RetryCount        = domain.RetryCount,
        LastError         = domain.LastError,
    };

    internal static SyncOutboxEntry ToDomain(SyncOutboxEntity entity) => new(
        Id:                entity.Id,
        BiometricEventId:  entity.BiometricEventId,
        CorrelationId:     entity.CorrelationId,
        SerializedPayload: entity.SerializedPayload,
        CreatedAt:         DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMs),
        ProcessedAt:       entity.ProcessedAtUnixMs.HasValue
                               ? DateTimeOffset.FromUnixTimeMilliseconds(entity.ProcessedAtUnixMs.Value)
                               : null,
        RetryCount:        entity.RetryCount,
        LastError:         entity.LastError);
}
