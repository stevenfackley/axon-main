using System.ComponentModel;
using System.Text.Json;
using Axon.Core.Domain;
using Axon.Core.Licensing;
using Axon.Core.Ports;
using Axon.Core.Serialization;
using Axon.Infrastructure.Security;
using Axon.UI.Application;
using Axon.UI.Observability;
using Axon.UI.ViewModels;
using LicenseContext = Axon.UI.Application.LicenseContext;

namespace Axon.Tests;

/// <summary>
/// Pins Air-Gap Mode end to end: whichever toggle the user flips (Settings panel or
/// shell), the HTTP guard, the outbox relay and both view models must agree, and an
/// air-gapped relay must never pull or send outbox entries.
/// The shell is built with a real <see cref="OutboxRelayService"/> over stubs and a
/// manual clock, so relay ticks are driven by the test.
/// </summary>
public sealed class AirGapTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SettingsToggleOn_AirGapsHttpRelayAndShell()
    {
        await using var h = new Harness();
        var shellChanges = Track(h.Shell);

        h.Shell.Settings.AirGapEnabled = true;

        Assert.True(h.State.Enabled);
        Assert.True(h.Shell.AirGapEnabled);
        Assert.True(h.Relay.Current.AirGapEnabled);
        Assert.Equal(RelayState.AirGapped, h.Relay.Current.State);
        Assert.Equal(SyncStatus.AirGapped, h.Shell.SyncStatus);
        Assert.Contains(nameof(MainWindowViewModel.AirGapEnabled), shellChanges);
    }

    [Fact]
    public async Task SettingsToggleOff_RestoresHttpRelayAndShell()
    {
        await using var h = new Harness();
        h.Shell.Settings.AirGapEnabled = true;

        h.Shell.Settings.AirGapEnabled = false;

        Assert.False(h.State.Enabled);
        Assert.False(h.Shell.AirGapEnabled);
        Assert.False(h.Relay.Current.AirGapEnabled);
        Assert.Equal(RelayState.Idle, h.Relay.Current.State);
        Assert.Equal(SyncStatus.Idle, h.Shell.SyncStatus);
    }

    [Fact]
    public async Task ShellToggle_IsMirroredInSettings()
    {
        await using var h = new Harness();
        var settingsChanges = Track(h.Shell.Settings);

        h.Shell.AirGapEnabled = true;

        Assert.True(h.Shell.Settings.AirGapEnabled);
        Assert.True(h.State.Enabled);
        Assert.True(h.Relay.Current.AirGapEnabled);
        Assert.Contains(nameof(SettingsViewModel.AirGapEnabled), settingsChanges);
    }

    [Fact]
    public async Task EnablingAirGap_ClearsTheLastRelayError()
    {
        await using var h = new Harness();
        h.Outbox.Pending.Add(PendingEntry());
        h.Transport.Accept = false;
        var errored = WaitForState(h.Relay, RelayState.Error);
        await h.Relay.StartAsync();

        h.Time.Tick();
        await errored.WaitAsync(Timeout);
        Assert.Equal("rejected by stub", h.Relay.Current.LastError);

        h.Shell.Settings.AirGapEnabled = true;

        Assert.Null(h.Relay.Current.LastError);
        Assert.Equal(RelayState.AirGapped, h.Relay.Current.State);
    }

    [Fact]
    public async Task AirGappedRelay_NeverPullsOrSendsOnTicks()
    {
        await using var h = new Harness();
        h.Outbox.Pending.Add(PendingEntry());
        await h.Relay.StartAsync();
        await h.Outbox.Counted.WaitAsync(Timeout); // the snapshot published by StartAsync

        h.Shell.Settings.AirGapEnabled = true;
        for (int i = 0; i < 2; i++)
        {
            h.Time.Tick();
            Assert.True(await h.Outbox.Counted.WaitAsync(Timeout), "relay loop did not tick");
        }

        Assert.Equal(0, h.Outbox.GetPendingCalls);
        Assert.Equal(0, h.Transport.SendCalls);
    }

    [Fact]
    public async Task DirectStateWrite_AlsoAirGapsTheRelayAndBothViewModels()
    {
        await using var h = new Harness();
        h.Outbox.Pending.Add(PendingEntry());
        await h.Relay.StartAsync();
        await h.Outbox.Counted.WaitAsync(Timeout);
        var shellChanges = Track(h.Shell);
        var settingsChanges = Track(h.Shell.Settings);

        h.State.Enabled = true;
        h.Time.Tick();
        Assert.True(await h.Outbox.Counted.WaitAsync(Timeout), "relay loop did not tick");
        h.Time.Tick();
        Assert.True(await h.Outbox.Counted.WaitAsync(Timeout), "relay loop did not tick");

        Assert.Equal(0, h.Outbox.GetPendingCalls);
        Assert.Equal(RelayState.AirGapped, h.Relay.Current.State);
        Assert.True(h.Shell.AirGapEnabled);
        Assert.True(h.Shell.Settings.AirGapEnabled);
        Assert.Contains(nameof(MainWindowViewModel.AirGapEnabled), shellChanges);
        Assert.Contains(nameof(SettingsViewModel.AirGapEnabled), settingsChanges);
    }

    [Fact]
    public async Task RelayNotAirGapped_SendsPendingEntriesOnATick()
    {
        await using var h = new Harness();
        h.Outbox.Pending.Add(PendingEntry());
        await h.Relay.StartAsync();

        h.Time.Tick();
        await h.Transport.FirstSend.Task.WaitAsync(Timeout);

        Assert.Equal(1, h.Transport.SendCalls);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<string?> Track(INotifyPropertyChanged source)
    {
        var names = new List<string?>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        return names;
    }

    private static Task WaitForState(OutboxRelayService relay, RelayState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.SnapshotChanged += (_, s) =>
        {
            if (s.State == state) reached.TrySetResult();
        };
        return reached.Task;
    }

    private static SyncOutboxEntry PendingEntry()
    {
        var evt = new BiometricEvent(
            Guid.NewGuid(), new DateTimeOffset(2026, 1, 15, 7, 0, 0, TimeSpan.Zero),
            BiometricType.HeartRate, 60, "bpm",
            new SourceMetadata("fixture-device", "Fixture", null, 1f, DateTimeOffset.UnixEpoch));
        return new SyncOutboxEntry(
            Guid.NewGuid(), evt.Id, "fixture-batch",
            JsonSerializer.Serialize(evt, AxonJsonContext.Default.BiometricEvent),
            DateTimeOffset.UnixEpoch, ProcessedAt: null, RetryCount: 0, LastError: null);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness()
        {
            Relay = new OutboxRelayService(Outbox, Transport, NullHealthReportWriter.Instance, State, Time);
            Shell = new MainWindowViewModel(
                new DashboardViewModel(null!),
                new AnalysisLabViewModel(null!, null!, null!),
                Relay,
                NullObservabilityRuntime.Instance,
                new WhoopSyncCoordinator(null!, null!, null!, null!, isConfigured: false),
                State,
                new DataImportCoordinator(null!),
                new LicenseContext(LicenseTier.Free),
                new DataWipeService(
                    Path.Combine(Path.GetTempPath(), "axon-airgap-" + Guid.NewGuid().ToString("N"), "Axon"),
                    new MockHardwareVault(), []));
        }

        public ManualTimeProvider Time { get; } = new();
        public StubOutbox Outbox { get; } = new();
        public StubTransport Transport { get; } = new();
        public AirGapState State { get; } = new();
        public OutboxRelayService Relay { get; }
        public MainWindowViewModel Shell { get; }

        public ValueTask DisposeAsync() => Relay.DisposeAsync();
    }

    /// <summary>A clock whose timers fire only when the test calls <see cref="Tick"/>.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        public void Tick()
        {
            ManualTimer[] timers;
            lock (_timers) timers = [.. _timers];
            foreach (var timer in timers) timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private volatile bool _disposed;

            public void Fire()
            {
                if (!_disposed) callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StubOutbox : ISyncOutboxRepository
    {
        public List<SyncOutboxEntry> Pending { get; } = [];

        /// <summary>Released once per CountPendingAsync call (one per published snapshot).</summary>
        public SemaphoreSlim Counted { get; } = new(0);

        public int GetPendingCalls;

        public ValueTask<IReadOnlyList<SyncOutboxEntry>> GetPendingAsync(int batchSize = 100, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetPendingCalls);
            return ValueTask.FromResult<IReadOnlyList<SyncOutboxEntry>>([.. Pending]);
        }

        public ValueTask<int> CountPendingAsync(CancellationToken ct = default)
        {
            Counted.Release();
            return ValueTask.FromResult(Pending.Count);
        }

        public ValueTask MarkProcessedAsync(Guid entryId, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask MarkFailedAsync(Guid entryId, string error, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask PurgeOldAsync(int retentionDays = 30, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<SyncOutboxEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => ValueTask.FromResult<SyncOutboxEntry?>(null);

        public ValueTask AddAsync(SyncOutboxEntry entity, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask AddRangeAsync(IReadOnlyList<SyncOutboxEntry> entities, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask UpdateAsync(SyncOutboxEntry entity, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class StubTransport : ISyncTransport
    {
        public bool Accept { get; set; } = true;

        public int SendCalls;

        public TaskCompletionSource FirstSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string TransportName => "stub";

        public ValueTask<SyncBatchAcknowledgement> SendAsync(SyncBatch batch, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SendCalls);
            FirstSend.TrySetResult();
            return ValueTask.FromResult(new SyncBatchAcknowledgement(
                batch.BatchId, Accept, Accept ? null : "rejected by stub", DateTimeOffset.UnixEpoch));
        }
    }
}
