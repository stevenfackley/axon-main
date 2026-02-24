using Axon.Core.Domain;

namespace Axon.Core.Ports;

/// <summary>
/// Port for the ingestion pipeline that coordinates driver fetch →
/// repository persist → inference trigger.
///
/// Implementations live in Axon.Infrastructure and must:
///   1. Persist events via <see cref="IBiometricRepository"/> within a transaction.
///   2. Write the corresponding <see cref="SyncOutboxEntry"/> in the same transaction
///      (Transactional Outbox pattern).
///   3. Enqueue an inference pass on the background work queue — never await
///      inference inside the DB transaction.
/// </summary>
public interface IIngestionOrchestrator
{
    /// <summary>
    /// Ingests all events from <paramref name="driver"/> newer than <paramref name="since"/>,
    /// persists them, and schedules an inference pass.
    /// </summary>
    ValueTask IngestAsync(
        IBiometricDriver driver,
        DateTimeOffset   since,
        CancellationToken ct = default);
}
