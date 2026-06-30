using Axon.Core.Domain;
using Axon.Infrastructure.Persistence.Entities;
using Axon.Infrastructure.Persistence.Mappers;

namespace Axon.Tests;

/// <summary>
/// Round-trip coverage for the pure domain↔EF-entity mappers. These are on the
/// ingestion hot path and must preserve every field across the epoch-millis
/// timestamp conversion. Timestamps are built from whole milliseconds so the
/// round-trip is bit-exact.
/// </summary>
public class PersistenceMapperTests
{
    private static DateTimeOffset Ms(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

    // ── BiometricEventMapper ────────────────────────────────────────────────────

    [Fact]
    public void BiometricEvent_RoundTrips_AllFields()
    {
        var domain = new BiometricEvent(
            Guid.NewGuid(),
            Ms(1_768_521_600_000),
            BiometricType.HeartRate,
            62.5,
            "bpm",
            new SourceMetadata("dev-1", "Whoop", "fw-1.2", 0.95f, Ms(1_768_521_700_000)),
            "corr-1");

        var back = BiometricEventMapper.ToDomain(BiometricEventMapper.ToEntity(domain));

        Assert.Equal(domain, back); // record value-equality covers nested SourceMetadata
    }

    [Fact]
    public void BiometricEvent_ToEntity_MapsScalarFields()
    {
        var domain = new BiometricEvent(
            Guid.NewGuid(),
            Ms(1_768_521_600_000),
            BiometricType.SpO2,
            98.0,
            "%",
            new SourceMetadata("dev-1", "Oura", null, 0.9f, Ms(1_768_521_700_000)),
            CorrelationId: null);

        var entity = BiometricEventMapper.ToEntity(domain);

        Assert.Equal((byte)BiometricType.SpO2, entity.BiometricType);
        Assert.Equal(1_768_521_600_000, entity.TimestampUnixMs);
        Assert.Equal(1_768_521_700_000, entity.IngestionTimestampUnixMs);
        Assert.Equal("%", entity.Unit);
        Assert.Null(entity.FirmwareVersion);
        Assert.Null(entity.CorrelationId);
    }

    [Fact]
    public void BiometricEvent_RoundTrips_WithNullOptionalFields()
    {
        var domain = new BiometricEvent(
            Guid.NewGuid(),
            Ms(1_700_000_000_000),
            BiometricType.Steps,
            8000,
            "steps",
            new SourceMetadata("dev-2", "Garmin", FirmwareVersion: null, 0.8f, Ms(1_700_000_050_000)),
            CorrelationId: null);

        var back = BiometricEventMapper.ToDomain(BiometricEventMapper.ToEntity(domain));

        Assert.Equal(domain, back);
        Assert.Null(back.Source.FirmwareVersion);
        Assert.Null(back.CorrelationId);
    }

    // ── AuditLogMapper ──────────────────────────────────────────────────────────

    [Fact]
    public void AuditLog_RoundTrips_AllFields()
    {
        var domain = new AuditLogEntry(
            Guid.NewGuid(),
            Ms(1_768_521_600_000),
            AuditOperation.Write,
            "BiometricRepository",
            "sha256:abc",
            "evt-42",
            "Ingested batch");

        var back = AuditLogMapper.ToDomain(AuditLogMapper.ToEntity(domain));

        Assert.Equal(domain, back);
    }

    [Fact]
    public void AuditLog_RoundTrips_WithNullAffectedEntity()
    {
        var domain = new AuditLogEntry(
            Guid.NewGuid(),
            Ms(1_768_521_600_000),
            AuditOperation.KeyAccess,
            "HardwareVault",
            "sha256:def",
            AffectedEntityId: null,
            "Derived encryption key");

        var entity = AuditLogMapper.ToEntity(domain);
        Assert.Equal((byte)AuditOperation.KeyAccess, entity.Operation);
        Assert.Null(entity.AffectedEntityId);

        Assert.Equal(domain, AuditLogMapper.ToDomain(entity));
    }

    // ── SyncOutboxMapper ────────────────────────────────────────────────────────

    [Fact]
    public void SyncOutbox_RoundTrips_WhenProcessed()
    {
        var domain = new SyncOutboxEntry(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "corr-1",
            "base64-ciphertext",
            Ms(1_768_521_600_000),
            ProcessedAt: Ms(1_768_521_660_000),
            RetryCount: 2,
            LastError: "timeout");

        var back = SyncOutboxMapper.ToDomain(SyncOutboxMapper.ToEntity(domain));

        Assert.Equal(domain, back);
    }

    [Fact]
    public void SyncOutbox_RoundTrips_WhenUnprocessed()
    {
        var domain = new SyncOutboxEntry(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "corr-2",
            "base64-ciphertext",
            Ms(1_768_521_600_000),
            ProcessedAt: null,
            RetryCount: 0,
            LastError: null);

        var entity = SyncOutboxMapper.ToEntity(domain);
        Assert.Null(entity.ProcessedAtUnixMs); // null-coalescing branch in ToEntity

        var back = SyncOutboxMapper.ToDomain(entity);
        Assert.Equal(domain, back);
        Assert.Null(back.ProcessedAt); // HasValue branch in ToDomain
    }
}
