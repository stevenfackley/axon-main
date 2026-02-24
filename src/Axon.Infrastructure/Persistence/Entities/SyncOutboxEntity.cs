namespace Axon.Infrastructure.Persistence.Entities;

/// <summary>
/// EF Core entity for the <c>SyncOutbox</c> table.
/// Written atomically with <see cref="BiometricEventEntity"/> — see
/// <see cref="Axon.Infrastructure.Persistence.BiometricRepository.IngestBatchAsync"/>.
/// </summary>
internal sealed class SyncOutboxEntity
{
    public Guid    Id                  { get; set; }
    public Guid    BiometricEventId    { get; set; }
    public string  CorrelationId       { get; set; }  = string.Empty;
    public string  SerializedPayload   { get; set; }  = string.Empty;  // AES-256 ciphertext base-64
    public long    CreatedAtUnixMs     { get; set; }
    public long?   ProcessedAtUnixMs   { get; set; }
    public int     RetryCount          { get; set; }
    public string? LastError           { get; set; }
}
