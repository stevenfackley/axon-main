using System.Text.Json;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Core.Serialization;
using Axon.UI.Observability;

namespace Axon.UI.Application;

internal sealed class OutboxRelayService : IOutboxRelayService
{
    private readonly ISyncOutboxRepository _outboxRepository;
    private readonly ISyncTransport _transport;
    private readonly IHealthReportWriter _healthReportWriter;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(15));
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _started;
    private RelaySnapshot _current;

    public OutboxRelayService(
        ISyncOutboxRepository outboxRepository,
        ISyncTransport transport,
        IHealthReportWriter healthReportWriter)
    {
        _outboxRepository = outboxRepository;
        _transport = transport;
        _healthReportWriter = healthReportWriter;
        _current = new RelaySnapshot(
            State: RelayState.Idle,
            PendingCount: 0,
            LastSuccessfulSync: null,
            LastError: null,
            AirGapEnabled: false,
            TransportName: transport.TransportName);
    }

    public event EventHandler<RelaySnapshot>? SnapshotChanged;

    public RelaySnapshot Current => _current;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;
        _started = true;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await PublishSnapshotAsync(ct);
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token), CancellationToken.None);
    }

    public void SetAirGapEnabled(bool enabled)
    {
        UpdateSnapshot(_current with
        {
            AirGapEnabled = enabled,
            State = enabled ? RelayState.AirGapped : RelayState.Idle,
            LastError = enabled ? null : _current.LastError
        });
    }

    public Task RefreshAsync(CancellationToken ct = default) =>
        PublishSnapshotAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_loopCts is not null)
        {
            _loopCts.Cancel();
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loopCts?.Dispose();
        _timer.Dispose();
        _syncLock.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await PublishSnapshotAsync(ct).ConfigureAwait(false);
                if (_current.AirGapEnabled)
                {
                    continue;
                }

                await DrainPendingAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DrainPendingAsync(CancellationToken ct)
    {
        using var activity = AxonObservability.ActivitySource.StartActivity("relay.drain");
        activity?.SetTag("relay.transport", _transport.TransportName);

        if (!await _syncLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            activity?.SetTag("relay.skipped", true);
            return;
        }

        try
        {
            var pending = await _outboxRepository.GetPendingAsync(batchSize: 32, ct).ConfigureAwait(false);
            activity?.SetTag("relay.pending_count", pending.Count);
            if (pending.Count == 0)
            {
                UpdateSnapshot(_current with { State = RelayState.Idle, LastError = null, PendingCount = 0 });
                return;
            }

            UpdateSnapshot(_current with { State = RelayState.Syncing, LastError = null, PendingCount = pending.Count });

            var batches = BuildBatches(pending);
            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();
                var ack = await _transport.SendAsync(batch, ct).ConfigureAwait(false);
                if (!ack.Accepted)
                {
                    AxonObservability.RelayFailureCounter.Add(1,
                        new KeyValuePair<string, object?>("transport", _transport.TransportName));
                    activity?.SetTag("relay.accepted", false);
                    activity?.SetTag("relay.error", ack.Message);

                    foreach (var entry in pending.Where(e => e.CorrelationId == batch.CorrelationId))
                    {
                        await _outboxRepository.MarkFailedAsync(entry.Id, ack.Message ?? "Transport rejected batch.", ct).ConfigureAwait(false);
                    }

                    UpdateSnapshot(_current with { State = RelayState.Error, LastError = ack.Message });
                    return;
                }

                foreach (var entry in pending.Where(e => e.CorrelationId == batch.CorrelationId))
                {
                    await _outboxRepository.MarkProcessedAsync(entry.Id, ct).ConfigureAwait(false);
                }

                UpdateSnapshot(_current with
                {
                    State = RelayState.Idle,
                    LastSuccessfulSync = ack.ProcessedAt,
                    LastError = null
                });

                AxonObservability.RelayBatchCounter.Add(1,
                    new KeyValuePair<string, object?>("transport", _transport.TransportName));
                AxonObservability.RelayEventCounter.Add(batch.Events.Count,
                    new KeyValuePair<string, object?>("transport", _transport.TransportName));
                activity?.SetTag("relay.accepted", true);
                activity?.SetTag("relay.batch_size", batch.Events.Count);
            }

            await PublishSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AxonObservability.RelayFailureCounter.Add(1,
                new KeyValuePair<string, object?>("transport", _transport.TransportName));
            activity?.SetTag("relay.exception", ex.GetType().Name);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            UpdateSnapshot(_current with { State = RelayState.Error, LastError = ex.Message });
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private IReadOnlyList<SyncBatch> BuildBatches(IReadOnlyList<SyncOutboxEntry> entries)
    {
        var grouped = entries.GroupBy(e => e.CorrelationId);
        var batches = new List<SyncBatch>();
        foreach (var group in grouped)
        {
            var events = new List<BiometricEvent>();
            foreach (var entry in group)
            {
                var evt = JsonSerializer.Deserialize(entry.SerializedPayload, AxonJsonContext.Default.BiometricEvent);
                if (evt is not null)
                {
                    events.Add(evt);
                }
            }

            if (events.Count == 0)
            {
                continue;
            }

            batches.Add(new SyncBatch(
                BatchId: Guid.NewGuid(),
                CorrelationId: group.Key,
                CreatedAt: group.Min(e => e.CreatedAt),
                Events: events));
        }

        return batches;
    }

    private async Task PublishSnapshotAsync(CancellationToken ct)
    {
        int pendingCount = await _outboxRepository.CountPendingAsync(ct).ConfigureAwait(false);
        RelayState state = _current.AirGapEnabled
            ? RelayState.AirGapped
            : pendingCount > 0 && _current.State == RelayState.Syncing
                ? RelayState.Syncing
                : _current.State == RelayState.Error
                    ? RelayState.Error
                    : RelayState.Idle;

        UpdateSnapshot(_current with
        {
            PendingCount = pendingCount,
            State = state
        });
    }

    private void UpdateSnapshot(RelaySnapshot snapshot)
    {
        _current = snapshot;
        AxonObservability.PendingOutboxHistogram.Record(snapshot.PendingCount,
            new KeyValuePair<string, object?>("state", snapshot.State.ToString()));
        _ = _healthReportWriter.WriteAsync(snapshot);
        SnapshotChanged?.Invoke(this, snapshot);
    }
}
