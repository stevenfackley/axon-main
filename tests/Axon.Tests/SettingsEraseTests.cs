using Axon.Core.Domain;
using Axon.Core.Licensing;
using Axon.Core.Ports;
using Axon.Infrastructure.Security;
using Axon.UI.Application;
using Axon.UI.Observability;
using Axon.UI.ViewModels;
using LicenseContext = Axon.UI.Application.LicenseContext;

namespace Axon.Tests;

/// <summary>
/// The "Erase all data" card in Settings and the shell around it. The Avalonia view itself
/// (the text box, the buttons, the shutdown on "Close Axon") is not exercised: no display.
/// </summary>
public sealed class SettingsEraseTests
{
    private static readonly DataWipeResult Done = new(["Encryption keys", "Logs"], [], []);

    private static readonly DataWipeResult Partial =
        new(["Encryption keys"], [@"logs\axon-20261127.log (The file is in use.)"], []);

    [Fact]
    public void EraseCommand_IsArmedOnlyByTheExactWord()
    {
        var settings = new SettingsViewModel(new AirGapState()) { EraseHandler = () => Task.FromResult(Done) };

        foreach (var typed in new[] { "", "erase", "Erase", " ERASE", "ERASE ", "ERASED" })
        {
            settings.EraseConfirmationText = typed;
            Assert.False(settings.EraseAllDataCommand.CanExecute(null), $"armed by '{typed}'");
        }

        settings.EraseConfirmationText = SettingsViewModel.EraseConfirmationWord;
        Assert.True(settings.EraseAllDataCommand.CanExecute(null));
    }

    [Fact]
    public void EraseCommand_WithoutAHandler_StaysDisarmed()
    {
        var settings = new SettingsViewModel(new AirGapState()) { EraseConfirmationText = "ERASE" };

        Assert.False(settings.EraseAllDataCommand.CanExecute(null));
    }

    [Fact]
    public void EraseCommand_Complete_SaysSo_AndOffersToClose()
    {
        var settings = Armed(() => Task.FromResult(Done));
        var closeRequested = false;
        settings.CloseAppRequested += (_, _) => closeRequested = true;
        Assert.False(settings.CloseAppCommand.CanExecute(null));

        settings.EraseAllDataCommand.Execute(null);

        Assert.True(settings.IsDataErased);
        Assert.True(settings.IsEraseComplete);
        Assert.StartsWith("Everything Axon stored on this device has been erased.", settings.EraseStatusText);
        Assert.Contains("Close Axon to finish", settings.EraseStatusText);
        Assert.False(settings.EraseAllDataCommand.CanExecute(null));

        Assert.True(settings.CloseAppCommand.CanExecute(null));
        settings.CloseAppCommand.Execute(null);
        Assert.True(closeRequested);
    }

    [Fact]
    public void EraseCommand_Partial_ListsWhatRemains_AndCanRunAgain()
    {
        var answers = new Queue<DataWipeResult>([Partial, Done]);
        var calls = 0;
        var settings = Armed(() =>
        {
            calls++;
            return Task.FromResult(answers.Dequeue());
        });

        settings.EraseAllDataCommand.Execute(null);

        Assert.True(settings.IsDataErased);
        Assert.False(settings.IsEraseComplete);
        Assert.Contains("Not everything could be erased", settings.EraseStatusText);
        Assert.Contains(@"logs\axon-20261127.log", settings.EraseStatusText);
        Assert.DoesNotContain("has been erased", settings.EraseStatusText);
        Assert.False(settings.CloseAppCommand.CanExecute(null));
        Assert.True(settings.EraseAllDataCommand.CanExecute(null));

        settings.EraseAllDataCommand.Execute(null);

        Assert.Equal(2, calls);
        Assert.True(settings.IsEraseComplete);
    }

    [Fact]
    public void EraseCommand_WhileAWhoopSyncRuns_ErasesNothing()
    {
        var calls = 0;
        var settings = Armed(() =>
        {
            calls++;
            return Task.FromResult(Done);
        });
        settings.IsWhoopBusy = true;

        settings.EraseAllDataCommand.Execute(null);

        Assert.Equal(0, calls);
        Assert.False(settings.IsDataErased);
        Assert.Contains("Nothing was erased", settings.EraseStatusText);
    }

    [Fact]
    public void EraseCommand_WhenTheHandlerThrows_NeverClaimsSuccess()
    {
        var settings = Armed(() => throw new IOException("disk gone"));

        settings.EraseAllDataCommand.Execute(null);

        Assert.False(settings.IsEraseComplete);
        Assert.Contains("disk gone", settings.EraseStatusText);
        Assert.Contains("Some data may still be on this device", settings.EraseStatusText);
    }

    [Fact]
    public void AfterAnEraseStarts_WhoopAndImportStayOff()
    {
        var imports = 0;
        var settings = Armed(() => Task.FromResult(Partial));
        settings.IsWhoopConfigured = true;
        settings.ImportHandler = _ =>
        {
            imports++;
            return Task.CompletedTask;
        };
        Assert.True(settings.ConnectWhoopCommand.CanExecute(null));

        settings.EraseAllDataCommand.Execute(null);
        _ = settings.RunImportAsync(["data.csv"]);

        Assert.False(settings.ConnectWhoopCommand.CanExecute(null));
        Assert.False(settings.SyncWhoopCommand.CanExecute(null));
        Assert.Equal(0, imports);
    }

    [Fact]
    public void EraseDescription_ClaimsTheKeysProtectLeftoversOnlyWithAHardwareVault()
    {
        var settings = new SettingsViewModel(new AirGapState());
        Assert.DoesNotContain("can no longer be decrypted", settings.EraseDescription);

        settings.IsHardwareBacked = true;
        Assert.Contains("can no longer be decrypted", settings.EraseDescription);
        Assert.Contains("Data in your Whoop account is not affected", settings.EraseDescription);
    }

    [Fact]
    public async Task Shell_Erase_LocksTheAppToSettings_AndErasesTheDataFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "axon-shell-erase-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(root, "Axon");
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
        File.WriteAllText(Path.Combine(dataDir, "logs", "axon-20261127.log"), "{}");
        try
        {
            var relay = new IdleRelay();
            var shell = new MainWindowViewModel(
                new DashboardViewModel(null!),
                new AnalysisLabViewModel(null!, null!, null!),
                relay,
                NullObservabilityRuntime.Instance,
                new WhoopSyncCoordinator(null!, null!, null!, null!, isConfigured: false),
                new AirGapState(),
                new DataImportCoordinator(null!),
                new LicenseContext(LicenseTier.Free),
                new DataWipeService(dataDir, new MockHardwareVault(),
                    [new("Stopping the sync relay", () => relay.DisposeAsync())]));
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.IsEraseComplete)) finished.TrySetResult();
            };
            shell.ActiveWorkspace = ShellWorkspace.Dashboard;
            shell.Settings.EraseConfirmationText = "ERASE";

            shell.Settings.EraseAllDataCommand.Execute(null);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ShellWorkspace.Settings, shell.ActiveWorkspace);
            Assert.False(shell.ShowDashboardCommand.CanExecute(null));
            Assert.False(shell.ShowAnalysisLabCommand.CanExecute(null));
            Assert.True(relay.Disposed);
            Assert.False(Directory.Exists(dataDir));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Relay_StoppedByTheErase_StaysStopped()
    {
        // Start-up can still be on its way to StartAsync when the erase stops the relay;
        // the database is closed by then, so the relay must not touch the outbox again.
        var outbox = new ClosedOutbox();
        var relay = new OutboxRelayService(outbox, new LoopbackSyncTransport(), NullHealthReportWriter.Instance, new AirGapState());

        await relay.DisposeAsync();
        await relay.StartAsync();
        await relay.RefreshAsync();
        await relay.DisposeAsync();

        Assert.Equal(0, outbox.Calls);
    }

    private static SettingsViewModel Armed(Func<Task<DataWipeResult>> handler) =>
        new(new AirGapState())
        {
            EraseHandler = handler,
            EraseConfirmationText = SettingsViewModel.EraseConfirmationWord,
        };

    /// <summary>An outbox whose database has been closed: every call counts, then fails.</summary>
    private sealed class ClosedOutbox : ISyncOutboxRepository
    {
        public int Calls { get; private set; }

        private InvalidOperationException Closed()
        {
            Calls++;
            return new InvalidOperationException("The vault is not open.");
        }

        public ValueTask<IReadOnlyList<SyncOutboxEntry>> GetPendingAsync(int batchSize = 100, CancellationToken ct = default) => throw Closed();
        public ValueTask<int> CountPendingAsync(CancellationToken ct = default) => throw Closed();
        public ValueTask MarkProcessedAsync(Guid entryId, CancellationToken ct = default) => throw Closed();
        public ValueTask MarkFailedAsync(Guid entryId, string error, CancellationToken ct = default) => throw Closed();
        public ValueTask PurgeOldAsync(int retentionDays = 30, CancellationToken ct = default) => throw Closed();
        public ValueTask<SyncOutboxEntry?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw Closed();
        public ValueTask AddAsync(SyncOutboxEntry entity, CancellationToken ct = default) => throw Closed();
        public ValueTask AddRangeAsync(IReadOnlyList<SyncOutboxEntry> entities, CancellationToken ct = default) => throw Closed();
        public ValueTask UpdateAsync(SyncOutboxEntry entity, CancellationToken ct = default) => throw Closed();
        public ValueTask DeleteAsync(Guid id, CancellationToken ct = default) => throw Closed();
    }

    private sealed class IdleRelay : IOutboxRelayService
    {
        public bool Disposed { get; private set; }

        public event EventHandler<RelaySnapshot>? SnapshotChanged { add { } remove { } }

        public RelaySnapshot Current { get; } =
            new(RelayState.Idle, 0, null, null, AirGapEnabled: false, "Test");

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
