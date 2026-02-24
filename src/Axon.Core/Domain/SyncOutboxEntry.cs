namespace Axon.Core.Domain;

/// <summary>
/// Represents a pending gRPC sync task persisted in the <c>SyncOutbox</c> table.
///
/// Transactional Outbox pattern guarantee:
///   • Written atomically in the SAME EF Core transaction as the originating
///     <see cref="BiometricEvent"/> write. Never persisted independently.
///   • The background <c>OutboxRelayService</c> polls this table, transmits
///     the payload, and flips <see cref="ProcessedAt"/> — all outside any
///     DB transaction to honour the "No I/O inside a transaction" guardrail.
/// </summary>
public sealed record SyncOutboxEntry(
    Guid            Id,
    Guid            BiometricEventId,
    string          CorrelationId,
    string          SerializedPayload,   // AES-256 ciphertext (base-64)
    DateTimeOffset  CreatedAt,
    DateTimeOffset? ProcessedAt,
    int             RetryCount,
    string?         LastError)
{
    /// <summary>PII Shield.</summary>
    public override string ToString() =>
        $"SyncOutboxEntry {{ Id={Id}, EventId={BiometricEventId}, Processed={ProcessedAt is not null} }}";
}
