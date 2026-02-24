using Axon.Core.Domain;
using Axon.Infrastructure.Persistence.Entities;

namespace Axon.Infrastructure.Persistence.Mappers;

/// <summary>
/// Bidirectional mapper between the <see cref="BiometricEvent"/> domain record
/// and the flat <see cref="BiometricEventEntity"/> EF Core entity.
///
/// All timestamp conversions use Unix epoch millis (Int64) to avoid SQLite
/// DateTimeOffset serialisation ambiguities across timezones.
///
/// Hot path: called per-event during batch ingestion. No allocations beyond
/// the required record/object construction; no LINQ; no reflection.
/// </summary>
internal static class BiometricEventMapper
{
    internal static BiometricEventEntity ToEntity(BiometricEvent domain) => new()
    {
        Id                      = domain.Id,
        TimestampUnixMs         = domain.Timestamp.ToUnixTimeMilliseconds(),
        BiometricType           = (byte)domain.Type,
        Value                   = domain.Value,
        Unit                    = domain.Unit,
        DeviceId                = domain.Source.DeviceId,
        Vendor                  = domain.Source.Vendor,
        FirmwareVersion         = domain.Source.FirmwareVersion,
        ConfidenceScore         = domain.Source.ConfidenceScore,
        IngestionTimestampUnixMs = domain.Source.IngestionTimestamp.ToUnixTimeMilliseconds(),
        CorrelationId           = domain.CorrelationId,
    };

    internal static BiometricEvent ToDomain(BiometricEventEntity entity) => new(
        Id:            entity.Id,
        Timestamp:     DateTimeOffset.FromUnixTimeMilliseconds(entity.TimestampUnixMs),
        Type:          (BiometricType)entity.BiometricType,
        Value:         entity.Value,
        Unit:          entity.Unit,
        Source:        new SourceMetadata(
                           DeviceId:           entity.DeviceId,
                           Vendor:             entity.Vendor,
                           FirmwareVersion:    entity.FirmwareVersion,
                           ConfidenceScore:    entity.ConfidenceScore,
                           IngestionTimestamp: DateTimeOffset.FromUnixTimeMilliseconds(
                                                   entity.IngestionTimestampUnixMs)),
        CorrelationId: entity.CorrelationId);
}
