using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Ingestion;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;
using Axon.UI.Application;
using Axon.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Axon.Tests;

/// <summary>
/// Start-up with an unfinished erase: the decision (every cell), and the screen that acts on
/// it, run against a real temporary data folder. App.axaml.cs only wires these together;
/// the window itself is not exercised (no display).
/// </summary>
public sealed class StartupEraseTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDir;

    public StartupEraseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "axon-startup-" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "Axon");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string DbPath => Path.Combine(_dataDir, AxonDbContextFactory.DbFileName);

    private string Marker => Path.Combine(_dataDir, AxonDataFolder.ErasePendingMarker);

    [Theory]
    [InlineData(false, false, false, "OpenNormally")]
    [InlineData(false, false, true, "OpenNormally")]
    [InlineData(false, true, false, "OpenNormally")]
    [InlineData(false, true, true, "OpenNormally")]
    [InlineData(true, true, true, "AskAgain")]       // key still here: the data is intact
    [InlineData(true, false, true, "FinishErase")]   // key gone: the database is unreadable anyway
    [InlineData(true, true, false, "AskAgain")]      // development vault: never silently
    [InlineData(true, false, false, "AskAgain")]
    public void Decide_EveryCell(bool marker, bool databaseKey, bool hardwareVault, string expected) =>
        Assert.Equal(Enum.Parse<StartupEraseAction>(expected), StartupErase.Decide(marker, databaseKey, hardwareVault));

    [Fact]
    public async Task AskAgain_CancelTheErase_RemovesOnlyTheMarker_AndOpensTheApp()
    {
        File.WriteAllText(DbPath, "the person's data");
        File.WriteAllBytes(Marker, []);
        var gate = NewGate(StartupEraseAction.AskAgain, hardwareVault: true);
        var opened = false;
        gate.ContinueRequested += (_, _) => opened = true;

        await gate.StartAsync();
        Assert.True(gate.IsAsking);
        Assert.Contains(_dataDir, gate.Message);
        Assert.Contains("did not get as far as your data", gate.Message);
        gate.CancelEraseCommand.Execute(null);

        Assert.True(opened);
        Assert.False(File.Exists(Marker));
        Assert.Equal("the person's data", File.ReadAllText(DbPath));
    }

    [Fact]
    public async Task AskAgain_EraseNow_NeedsTheWord_ThenErases_AndSaysSoBeforeOpening()
    {
        File.WriteAllText(DbPath, "db");
        File.WriteAllBytes(Marker, []);
        var gate = NewGate(StartupEraseAction.AskAgain, hardwareVault: false);
        var opened = false;
        gate.ContinueRequested += (_, _) => opened = true;
        Assert.Contains("cannot tell how far the erase got", gate.Message);

        Assert.False(gate.EraseNowCommand.CanExecute(null));
        gate.ConfirmationText = "erase";
        Assert.False(gate.EraseNowCommand.CanExecute(null));
        gate.ConfirmationText = "ERASE";
        Assert.True(gate.EraseNowCommand.CanExecute(null));

        await RunCommandAsync(gate, gate.EraseNowCommand);

        Assert.True(gate.CanContinue);
        Assert.False(opened);   // told first
        Assert.StartsWith("The erase you started earlier has finished.", gate.Message);
        Assert.False(File.Exists(DbPath));
        gate.ContinueCommand.Execute(null);
        Assert.True(opened);
        Assert.False(gate.ContinueCommand.CanExecute(null));   // opens once
    }

    [Fact]
    public async Task FinishErase_FinishesWithoutAsking_ThenTellsThePerson()
    {
        File.WriteAllText(DbPath, "unreadable without its key");
        File.WriteAllBytes(Marker, []);
        var gate = NewGate(StartupEraseAction.FinishErase, hardwareVault: true);

        await gate.StartAsync();

        Assert.False(gate.IsAsking);
        Assert.True(gate.CanContinue);
        Assert.StartsWith("The erase you started earlier has finished.", gate.Message);
        Assert.False(File.Exists(DbPath));
        Assert.False(DataWipeService.IsPending(_dataDir));
    }

    [WindowsFact]
    public async Task FinishErase_ThatCannotFinish_KeepsTheAppClosed_AndOffersToTryAgain()
    {
        File.WriteAllText(DbPath, "db");
        File.WriteAllBytes(Marker, []);
        var gate = NewGate(StartupEraseAction.FinishErase, hardwareVault: true);

        using (new FileStream(DbPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await gate.StartAsync();
        }

        Assert.True(gate.CanRetry);
        Assert.False(gate.CanContinue);
        Assert.Contains("will not open", gate.Message);
        Assert.Contains(AxonDbContextFactory.DbFileName, gate.Message);

        await RunCommandAsync(gate, gate.RetryCommand);

        Assert.True(gate.CanContinue);
        Assert.False(File.Exists(DbPath));
    }

    private StartupEraseViewModel NewGate(StartupEraseAction action, bool hardwareVault) =>
        new(action,
            new DataWipeService(_dataDir, new MockHardwareVault(), [], retryDelay: TimeSpan.FromMilliseconds(10)),
            hardwareVault);

    /// <summary>Runs an async command and waits until the screen has moved on from "Erasing…".</summary>
    private static async Task RunCommandAsync(StartupEraseViewModel gate, ICommand command)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StartupEraseViewModel.CanContinue) or nameof(StartupEraseViewModel.CanRetry)
                && (gate.CanContinue || gate.CanRetry))
            {
                done.TrySetResult();
            }
        };
        command.Execute(null);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>The tracker the erase uses to stop work on the vault, and the work it stops.</summary>
public sealed class VaultActivityTests
{
    [Fact]
    public async Task Quiesce_CancelsRunningWork_WaitsForIt_AndRefusesNewWork()
    {
        var activity = new VaultActivity();
        var operation = activity.TryBegin("the dashboard load")!;
        var work = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, operation.Token); }
            catch (OperationCanceledException) { }
            finally { operation.Dispose(); }
        });

        var stillRunning = await activity.QuiesceAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(stillRunning);
        Assert.True(work.IsCompleted);
        Assert.Null(activity.TryBegin("a CSV import"));
    }

    [Fact]
    public async Task Quiesce_WorkThatOutlivesTheBound_IsNamed_AndWorkIsAllowedAgain()
    {
        var activity = new VaultActivity();
        using var stubborn = activity.TryBegin("a Whoop sync")!;

        var stillRunning = await activity.QuiesceAsync(TimeSpan.FromMilliseconds(50));

        Assert.Equal(["a Whoop sync"], stillRunning);
        using var next = activity.TryBegin("the dashboard load");
        Assert.NotNull(next);
    }

    [Fact]
    public async Task PostIngestPass_IsTracked_SoTheEraseCanStopIt()
    {
        var activity = new VaultActivity();
        var inference = new BlockingInference();
        var orchestrator = new IngestionOrchestrator(
            new OneEventRepository(), inference,
            NullLogger<IngestionOrchestrator>.Instance,
            activity);

        await orchestrator.IngestAsync(new OneEventDriver(), DateTimeOffset.UtcNow.AddDays(-1));
        await inference.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));   // the pass is running

        var stillRunning = await activity.QuiesceAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(stillRunning);
        Assert.True(inference.SawCancellation);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await orchestrator.IngestAsync(new OneEventDriver(), DateTimeOffset.UtcNow.AddDays(-1)));
    }

    private sealed class BlockingInference : IInferenceService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; private set; }

        public async ValueTask<IReadOnlyList<AnomalyResult>> DetectAnomaliesAsync(
            IReadOnlyList<BiometricEvent> heartRateSamples,
            IReadOnlyList<BiometricEvent> hrvSamples,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            return [];
        }

        public ValueTask<IReadOnlyList<ForecastPoint>> ForecastRecoveryAsync(
            IReadOnlyList<BiometricEvent> sleepHistory,
            IReadOnlyList<BiometricEvent> strainHistory,
            int horizonDays = 7,
            CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<ForecastPoint>>([]);
    }

    private sealed class OneEventDriver : IBiometricDriver
    {
        public string DriverId => "test";
        public string DisplayName => "Test";
        public bool SupportsOffline => true;

        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask AuthoriseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<BiometricEvent> FetchSinceAsync(
            DateTimeOffset since,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var ts = DateTimeOffset.UtcNow;
            yield return new BiometricEvent(Guid.NewGuid(), ts, BiometricType.HeartRate, 60, "bpm",
                new SourceMetadata("dev", "Test", null, 1f, ts));
        }

        public IAsyncEnumerable<BiometricEvent> StreamLiveAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Accepts writes and returns nothing on reads; only the orchestrator's flow matters here.</summary>
    private sealed class OneEventRepository : IBiometricRepository
    {
        public ValueTask IngestBatchAsync(IReadOnlyList<BiometricEvent> events, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<BiometricEvent>>([]);

        public IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, int bucketSizeSeconds,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<BiometricEvent?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask AddAsync(BiometricEvent entity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask AddRangeAsync(IReadOnlyList<BiometricEvent> entities, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask UpdateAsync(BiometricEvent entity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
