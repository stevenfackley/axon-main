using System.Buffers;
using System.Threading.Channels;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Microsoft.Extensions.Logging;

namespace Axon.Infrastructure.Ingestion;

/// <summary>
/// Coordinates the full ingestion pipeline:
///   Driver.FetchSinceAsync → IngestBatchAsync (atomic event + outbox) → Schedule Inference.
///
/// The pipeline uses <see cref="Channel{T}"/> so the driver's async enumerable
/// is decoupled from the persistence write path, preventing large payloads from
/// being buffered in managed memory. I/O is never performed inside a DB transaction.
///
/// Inference is fired via <see cref="Task.Run"/> after the last batch commits —
/// it is a deliberate fire-and-forget with structured error capture.
/// </summary>
public sealed class IngestionOrchestrator : IIngestionOrchestrator
{
    private const int ChannelCapacity     = 512;
    private const int PersistBatchSize    = 64;
    private const int InferenceWindowDays = 30;   // Look-back window fed to inference after ingest.

    private readonly IBiometricRepository          _repository;
    private readonly IInferenceService             _inference;
    private readonly ILogger<IngestionOrchestrator> _logger;

    public IngestionOrchestrator(
        IBiometricRepository           repository,
        IInferenceService              inference,
        ILogger<IngestionOrchestrator> logger)
    {
        _repository = repository;
        _inference  = inference;
        _logger     = logger;
    }

    /// <inheritdoc/>
    public async ValueTask IngestAsync(
        IBiometricDriver  driver,
        DateTimeOffset    since,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[Ingestion] Starting ingest from driver={DriverId} since={Since:O}.",
            driver.DriverId, since);

        // Bounded channel decouples the async enumerable producer from the batch-persist consumer.
        var channel = Channel.CreateBounded<BiometricEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode     = BoundedChannelFullMode.Wait
        });

        // Producer: drain the driver's lazy async enumerable into the channel.
        var producerTask = ProduceAsync(driver, since, channel.Writer, ct);

        // Consumer: read from channel in batches and persist atomically.
        int totalIngested = await ConsumeAsync(channel.Reader, ct).ConfigureAwait(false);

        await producerTask.ConfigureAwait(false);

        _logger.LogInformation(
            "[Ingestion] Completed ingest from {DriverId}: {Count} events persisted.",
            driver.DriverId, totalIngested);

        // Trigger inference pass on background thread — non-blocking for the caller.
        if (totalIngested > 0)
            TriggerInferencePass(driver.DriverId, ct);
    }

    // ── Producer ──────────────────────────────────────────────────────────────

    private static async Task ProduceAsync(
        IBiometricDriver              driver,
        DateTimeOffset                since,
        ChannelWriter<BiometricEvent> writer,
        CancellationToken             ct)
    {
        try
        {
            await foreach (var evt in driver.FetchSinceAsync(since, ct).ConfigureAwait(false))
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
        finally
        {
            writer.Complete();
        }
    }

    // ── Consumer ──────────────────────────────────────────────────────────────

    private async Task<int> ConsumeAsync(
        ChannelReader<BiometricEvent> reader,
        CancellationToken             ct)
    {
        int totalCount = 0;

        // Rent a batch buffer from the pool — zero heap allocation in the hot path.
        BiometricEvent[] batchBuffer = ArrayPool<BiometricEvent>.Shared.Rent(PersistBatchSize);

        try
        {
            int batchCount = 0;

            await foreach (var evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                batchBuffer[batchCount++] = evt;

                if (batchCount == PersistBatchSize)
                {
                    await PersistBatchAsync(batchBuffer, batchCount, ct).ConfigureAwait(false);
                    totalCount += batchCount;
                    batchCount  = 0;
                }
            }

            // Flush remainder.
            if (batchCount > 0)
            {
                await PersistBatchAsync(batchBuffer, batchCount, ct).ConfigureAwait(false);
                totalCount += batchCount;
            }
        }
        finally
        {
            ArrayPool<BiometricEvent>.Shared.Return(batchBuffer, clearArray: false);
        }

        return totalCount;
    }

    // ── Persistence (Transactional Outbox via IngestBatchAsync) ───────────────

    /// <summary>
    /// Delegates to <see cref="IBiometricRepository.IngestBatchAsync"/> which writes
    /// both the <see cref="BiometricEvent"/> records and their <see cref="SyncOutboxEntry"/>
    /// counterparts atomically in a single EF Core SaveChanges call.
    ///
    /// I/O (gRPC dispatch) is deliberately NOT performed here — only the outbox row
    /// is written, keeping the transaction short and free of network I/O.
    /// </summary>
    private async ValueTask PersistBatchAsync(
        BiometricEvent[] batch,
        int              count,
        CancellationToken ct)
    {
        // Build a List<BiometricEvent> from the rented array slice (no heap alloc beyond List).
        var events = new List<BiometricEvent>(count);
        for (int i = 0; i < count; i++)
            events.Add(batch[i]);

        // IngestBatchAsync opens a transaction, writes events + outbox entries, commits.
        await _repository.IngestBatchAsync(events, ct).ConfigureAwait(false);

        _logger.LogDebug("[Ingestion] Persisted batch of {Count} events.", count);
    }

    // ── Inference Trigger ─────────────────────────────────────────────────────

    /// <summary>
    /// Fires an inference pass in the background after a successful ingest.
    /// Uses <see cref="Task.Run"/> so it never blocks the ingestion caller.
    /// Errors are captured and logged — they must not surface to the caller.
    /// </summary>
    private void TriggerInferencePass(string driverId, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation(
                    "[Inference] Triggering post-ingest inference pass for driver={DriverId}.",
                    driverId);

                var since = DateTimeOffset.UtcNow.AddDays(-InferenceWindowDays);
                var now   = DateTimeOffset.UtcNow;

                // Pull HR + HRV for spike detection.
                var hrSamples = await _repository.QueryRangeAsync(
                    BiometricType.HeartRate, since, now, ct)
                    .ConfigureAwait(false);

                var hrvSamples = await _repository.QueryRangeAsync(
                    BiometricType.HeartRateVariability, since, now, ct)
                    .ConfigureAwait(false);

                var anomalies = await _inference.DetectAnomaliesAsync(hrSamples, hrvSamples, ct)
                    .ConfigureAwait(false);

                int flagged = anomalies.Count(a => a.IsAnomaly);
                _logger.LogInformation(
                    "[Inference] Spike detection complete: {Flagged}/{Total} anomalies detected.",
                    flagged, anomalies.Count);

                // Pull sleep + strain for recovery forecast.
                var sleepHistory = await _repository.QueryRangeAsync(
                    BiometricType.SleepEfficiency, since, now, ct)
                    .ConfigureAwait(false);

                var strainHistory = await _repository.QueryRangeAsync(
                    BiometricType.StrainScore, since, now, ct)
                    .ConfigureAwait(false);

                var forecast = await _inference.ForecastRecoveryAsync(
                    sleepHistory, strainHistory, horizonDays: 7, ct)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "[Inference] Recovery forecast complete: {Days} days projected.",
                    forecast.Count);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — do not log as error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[Inference] Post-ingest inference pass failed for driver={DriverId}.",
                    driverId);
            }
        }, ct);
    }
}
