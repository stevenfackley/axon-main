namespace Axon.Core.Domain;

/// <summary>
/// Transport contract for a batch of locally persisted biometric events.
/// </summary>
public sealed record SyncBatch(
    Guid BatchId,
    string CorrelationId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<BiometricEvent> Events);
