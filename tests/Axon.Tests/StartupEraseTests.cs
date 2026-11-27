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

    // Every cell: marker x database key state x hardware vault x database file present.
    // Unknown never becomes "gone"; a key that no longer works is treated like a missing one,
    // except that with no marker and no database a missing key is simply a first run.
    [Theory]
    [InlineData(false, "Missing", false, false, "OpenNormally")]
    [InlineData(false, "Missing", false, true, "OpenNormally")]
    [InlineData(false, "Missing", true, false, "OpenNormally")]
    [InlineData(false, "Missing", true, true, "KeyUnusable")]
    [InlineData(false, "Usable", false, false, "OpenNormally")]
    [InlineData(false, "Usable", false, true, "OpenNormally")]
    [InlineData(false, "Usable", true, false, "OpenNormally")]
    [InlineData(false, "Usable", true, true, "OpenNormally")]
    [InlineData(false, "Unusable", false, false, "OpenNormally")]
    [InlineData(false, "Unusable", false, true, "OpenNormally")]
    [InlineData(false, "Unusable", true, false, "KeyUnusable")]
    [InlineData(false, "Unusable", true, true, "KeyUnusable")]
    [InlineData(false, "Unknown", false, false, "KeyUnreadable")]
    [InlineData(false, "Unknown", false, true, "KeyUnreadable")]
    [InlineData(false, "Unknown", true, false, "KeyUnreadable")]
    [InlineData(false, "Unknown", true, true, "KeyUnreadable")]
    [InlineData(true, "Missing", false, false, "AskAgain")]
    [InlineData(true, "Missing", false, true, "AskAgain")]
    [InlineData(true, "Missing", true, false, "FinishErase")]
    [InlineData(true, "Missing", true, true, "FinishErase")]
    [InlineData(true, "Usable", false, false, "AskAgain")]
    [InlineData(true, "Usable", false, true, "AskAgain")]
    [InlineData(true, "Usable", true, false, "AskAgain")]
    [InlineData(true, "Usable", true, true, "AskAgain")]
    [InlineData(true, "Unusable", false, false, "AskAgain")]
    [InlineData(true, "Unusable", false, true, "AskAgain")]
    [InlineData(true, "Unusable", true, false, "FinishErase")]
    [InlineData(true, "Unusable", true, true, "FinishErase")]
    [InlineData(true, "Unknown", false, false, "KeyUnreadable")]
    [InlineData(true, "Unknown", false, true, "KeyUnreadable")]
    [InlineData(true, "Unknown", true, false, "KeyUnreadable")]
    [InlineData(true, "Unknown", true, true, "KeyUnreadable")]
    public void Decide_EveryCell(bool marker, string databaseKey, bool hardwareVault, bool database, string expected) =>
        Assert.Equal(
            Enum.Parse<StartupEraseAction>(expected),
            StartupErase.Decide(marker, Enum.Parse<VaultKeyState>(databaseKey), hardwareVault, database));

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
        Assert.Contains("your data has not been erased", gate.Message);
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

    [Fact]
    public async Task KeyUnusable_OffersAFreshStartOrClose_NeverToOpenTheOldData()
    {
        File.WriteAllText(DbPath, "unreadable");
        var gate = NewGate(StartupEraseAction.KeyUnusable, hardwareVault: true);
        var closed = false;
        gate.CloseRequested += (_, _) => closed = true;

        Assert.Contains("cannot be read, so the data stored here cannot be opened", gate.Message);
        Assert.True(gate.IsAsking);
        Assert.False(gate.CanCancelErase);
        Assert.True(gate.CanClose);
        gate.CloseCommand.Execute(null);
        Assert.True(closed);

        gate.ConfirmationText = "ERASE";
        await RunCommandAsync(gate, gate.EraseNowCommand);

        Assert.True(gate.CanContinue);
        Assert.StartsWith("What was left has been erased.", gate.Message);
        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void KeyUnreadable_ErasesNothing_AndTryAgainLooksAgain()
    {
        File.WriteAllText(DbPath, "the person's data");
        var next = StartupEraseAction.KeyUnreadable;
        var gate = NewGate(StartupEraseAction.KeyUnreadable, hardwareVault: true, () => next);
        var opened = false;
        gate.ContinueRequested += (_, _) => opened = true;

        Assert.Contains("Nothing has been erased", gate.Message);
        Assert.False(gate.IsAsking);
        Assert.True(gate.CanRecheck);
        Assert.True(gate.CanClose);

        gate.RecheckCommand.Execute(null);   // still unreadable
        Assert.False(opened);
        Assert.True(gate.CanRecheck);

        next = StartupEraseAction.OpenNormally;   // whatever held it let go
        gate.RecheckCommand.Execute(null);
        Assert.True(opened);
        Assert.True(File.Exists(DbPath));
    }

    // ── The key state start-up decides on (real DPAPI blobs) ─────────────────

    [WindowsFact]
    public async Task KeyState_ReadsTheBlob_NotJustItsName()
    {
        var keys = Path.Combine(_dataDir, AxonDataFolder.KeysFolder);
        Directory.CreateDirectory(keys);
        var vault = new WindowsDataProtectionVault(keys);
        var label = AxonDbContextFactory.KeyLabel;
        var blob = Path.Combine(keys, WindowsDataProtectionVault.KeyFileName(label));

        Assert.Equal(VaultKeyState.Missing, vault.GetKeyState(label));

        vault.ZeroKey(await vault.DeriveKeyAsync(label));
        Assert.Equal(VaultKeyState.Usable, vault.GetKeyState(label));

        using (new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(VaultKeyState.Unknown, vault.GetKeyState(label));   // locked: not "gone"

        File.WriteAllBytes(blob, new byte[new FileInfo(blob).Length]);   // zeroed, as a failed destroy leaves it
        Assert.Equal(VaultKeyState.Unusable, vault.GetKeyState(label));

        File.WriteAllBytes(blob, []);
        Assert.Equal(VaultKeyState.Unusable, vault.GetKeyState(label));
    }

    [WindowsFact]
    public async Task KeyState_UnreadableKeysFolder_IsUnknown_NeverGone()
    {
        var keys = Path.Combine(_dataDir, AxonDataFolder.KeysFolder);
        Directory.CreateDirectory(keys);
        var vault = new WindowsDataProtectionVault(keys);
        vault.ZeroKey(await vault.DeriveKeyAsync(AxonDbContextFactory.KeyLabel));
        var user = Environment.UserName;
        Assert.True(Icacls(keys, "/deny", $"{user}:(OI)(CI)(R)"));
        try
        {
            Assert.Equal(VaultKeyState.Unknown, vault.GetKeyState(AxonDbContextFactory.KeyLabel));
            Assert.Equal(StartupEraseAction.KeyUnreadable,
                StartupErase.Decide(markerPresent: true, vault.GetKeyState(AxonDbContextFactory.KeyLabel), true, true));
        }
        finally
        {
            Icacls(keys, "/remove:d", user);
        }
    }

    [WindowsFact]
    public async Task HandZeroedDatabaseKey_FinishesWithAMarker_AndOffersAFreshStartWithout()
    {
        var keys = Path.Combine(_dataDir, AxonDataFolder.KeysFolder);
        Directory.CreateDirectory(keys);
        var vault = new WindowsDataProtectionVault(keys);
        vault.ZeroKey(await vault.DeriveKeyAsync(AxonDbContextFactory.KeyLabel));
        var blob = Path.Combine(keys, WindowsDataProtectionVault.KeyFileName(AxonDbContextFactory.KeyLabel));
        File.WriteAllBytes(blob, new byte[new FileInfo(blob).Length]);
        File.WriteAllText(DbPath, "db");
        var state = vault.GetKeyState(AxonDbContextFactory.KeyLabel);

        Assert.Equal(StartupEraseAction.FinishErase, StartupErase.Decide(true, state, true, true));
        Assert.Equal(StartupEraseAction.KeyUnusable, StartupErase.Decide(false, state, true, true));
    }

    private static bool Icacls(string path, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(path);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(10_000);
        return process.HasExited && process.ExitCode == 0;
    }

    private StartupEraseViewModel NewGate(
        StartupEraseAction action, bool hardwareVault, Func<StartupEraseAction>? recheck = null) =>
        new(action,
            new DataWipeService(_dataDir, new MockHardwareVault(), [], retryDelay: TimeSpan.FromMilliseconds(10)),
            hardwareVault,
            recheck ?? (() => action));

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
