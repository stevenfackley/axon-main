namespace Axon.Infrastructure.Persistence.Entities;

/// <summary>
/// EF Core entity class for <c>BiometricEvents</c> table.
/// Kept deliberately flat so EF can map it without complex owned-entity reflection
/// chains that degrade AOT compatibility. Domain↔Entity translation lives in
/// <see cref="Axon.Infrastructure.Persistence.Mappers.BiometricEventMapper"/>.
/// </summary>
internal sealed class BiometricEventEntity
{
    public Guid   Id                  { get; set; }
    public long   TimestampUnixMs     { get; set; }   // UTC epoch millis — avoids SQLite DateTimeOffset quirks
    public byte   BiometricType       { get; set; }   // Stored as byte for compact indexing
    public double Value               { get; set; }
    public string Unit                { get; set; }   = string.Empty;

    // ── SourceMetadata (flattened) ────────────────────────────────────────────
    public string  DeviceId           { get; set; }   = string.Empty;   // AES-256 encrypted at rest via EncryptionDecorator
    public string  Vendor             { get; set; }   = string.Empty;
    public string? FirmwareVersion    { get; set; }
    public float   ConfidenceScore    { get; set; }
    public long    IngestionTimestampUnixMs { get; set; }

    public string? CorrelationId      { get; set; }
}
