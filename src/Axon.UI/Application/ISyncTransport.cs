using Axon.Core.Domain;

namespace Axon.UI.Application;

/// <summary>
/// Transport boundary for relaying batches to a future satellite or peer node.
/// </summary>
internal interface ISyncTransport
{
    string TransportName { get; }

    ValueTask<SyncBatchAcknowledgement> SendAsync(
        SyncBatch batch,
        CancellationToken ct = default);
}
