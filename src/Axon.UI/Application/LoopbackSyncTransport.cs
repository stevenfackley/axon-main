using Axon.Core.Domain;

namespace Axon.UI.Application;

/// <summary>
/// Development transport that accepts batches locally until gRPC is wired.
/// </summary>
internal sealed class LoopbackSyncTransport : ISyncTransport
{
    public string TransportName => "Loopback Relay";

    public ValueTask<SyncBatchAcknowledgement> SendAsync(
        SyncBatch batch,
        CancellationToken ct = default) =>
        ValueTask.FromResult(new SyncBatchAcknowledgement(
            BatchId: batch.BatchId,
            Accepted: true,
            Message: $"Accepted {batch.Events.Count} events locally.",
            ProcessedAt: DateTimeOffset.UtcNow));
}
