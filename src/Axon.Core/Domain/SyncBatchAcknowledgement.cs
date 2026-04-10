namespace Axon.Core.Domain;

/// <summary>
/// Result returned by the sync transport after attempting to relay a batch.
/// </summary>
public sealed record SyncBatchAcknowledgement(
    Guid BatchId,
    bool Accepted,
    string? Message,
    DateTimeOffset ProcessedAt);
