using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Axon.Core.Licensing;
using Axon.Core.Ports;
using Axon.Infrastructure.Configuration;
using Axon.Infrastructure.Drivers.Whoop;
using Axon.Infrastructure.Ingestion;
using Axon.Infrastructure.ML;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Persistence.Decorators;
using Axon.Infrastructure.Security;
using Axon.UI.Application;
using Axon.UI.ViewModels;
using Axon.UI.Views;
using Microsoft.Extensions.Logging;
#if ANDROID || IOS
using Microsoft.Extensions.Logging.Abstractions;
#endif
using Axon.UI.Observability;  // NullObservabilityRuntime / NullHealthReportWriter needed on all platforms
#if !ANDROID && !IOS
using Axon.UI.Logging;
#endif

// Alias to avoid clash with Android.App.Application on the android TFM.
using AvaloniaApp = Avalonia.Application;

namespace Axon.UI;

/// <summary>
/// Avalonia Application root. Wires the MVVM shell for both desktop and Android
/// lifetimes without importing any platform-specific types at this layer.
/// </summary>
public sealed class App : AvaloniaApp
{
    private AppRuntime? _runtime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Axon");

        // An erase that was cut short (a locked file, the app closed half-way) is finished
        // before anything opens the data again. If it still cannot finish, say what is left
        // and open nothing: the keys are gone, so the old vault could not be opened anyway.
        if (DataWipeService.IsPending(dataDirectory))
        {
            var pending = new DataWipeService(dataDirectory, CreateVault(dataDirectory), []);
            var result = Task.Run(pending.WipeAsync).GetAwaiter().GetResult();
            if (!result.Complete)
            {
                ShowUnfinishedErase(result);
                base.OnFrameworkInitializationCompleted();
                return;
            }
        }

        _runtime = BuildRuntime(dataDirectory);
        var vm = _runtime.MainWindowViewModel;

        switch (ApplicationLifetime)
        {
            // Desktop (Windows / Linux / macOS)
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = vm };
                desktop.MainWindow.Opened += async (_, _) => await vm.InitializeAsync();
                vm.Settings.CloseAppRequested += (_, _) => desktop.Shutdown();
                desktop.Exit += (_, _) => _runtime.Dispose();
                break;

            // Android (single-activity model via ISingleViewApplicationLifetime)
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = vm.Dashboard };
                _ = vm.InitializeAsync();
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The only window when an earlier erase could not be finished at start-up.</summary>
    private void ShowUnfinishedErase(DataWipeResult result)
    {
        var message = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = "Axon has not finished erasing your data, so it will not open. " +
                       "Starting Axon again retries the erase.\n\n" + result.Describe(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(24),
            },
        };

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window { Title = "Axon", Width = 640, Height = 420, Content = message };
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = message;
                break;
        }
    }

    /// <summary>
    /// Real hardware-backed key vault on Windows (DPAPI/TPM); dev mock elsewhere.
    /// NOTE: switching vaults changes key derivation — a database created under the
    /// mock vault cannot be opened under DPAPI. Delete %LOCALAPPDATA%/Axon to reset.
    /// </summary>
    private static IHardwareVault CreateVault(string dataDirectory)
    {
        var keysDir = Path.Combine(dataDirectory, "keys");
        return OperatingSystem.IsWindows()
            ? new WindowsDataProtectionVault(keysDir)
            : new MockHardwareVault();
    }

    private static AppRuntime BuildRuntime(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);

        // ── Logging ──────────────────────────────────────────────────────────
        // Desktop: full Serilog pipeline (JSON console + daily rolling file).
        // Mobile: fall back to NullLoggerFactory — platform logging is handled
        //         by the OS (logcat / Unified Logging).
#if !ANDROID && !IOS
        ILoggerFactory loggerFactory = SerilogBootstrapper.CreateLoggerFactory(dataDirectory);
        var observabilityRuntime = AxonObservabilityRuntime.Create(dataDirectory);
        var healthReportWriter = observabilityRuntime.HealthReportWriter;
#else
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        var observabilityRuntime = NullObservabilityRuntime.Instance;
        var healthReportWriter = NullHealthReportWriter.Instance;
#endif

        Directory.CreateDirectory(Path.Combine(dataDirectory, "keys"));
        IHardwareVault vault = CreateVault(dataDirectory);

        // Opens and migrates the vault once; every repository call then gets its own
        // short-lived DbContext from the factory.
        var db = new AxonDbContextFactory(vault);
        db.InitialiseAsync(dataDirectory).AsTask().GetAwaiter().GetResult();

        // Mandatory decorator chain (outer → inner): Audit → Encryption → concrete repo.
        // Every biometric write is AES-256-GCM field-encrypted and audit-logged — no bypass.
        var encryptedRepository = new EncryptionDecorator(new BiometricRepository(db), vault);
        IBiometricRepository biometricRepository = new AuditLoggingDecorator(
            encryptedRepository, new DbAuditLogger(db), callerIdentity: Environment.UserName);
        var syncOutboxRepository = new SyncOutboxRepository(db);
        var inferenceService = new LocalInferenceService(loggerFactory.CreateLogger<LocalInferenceService>());
        var syncTransport = new LoopbackSyncTransport();
        // Air-gap enforcement: one shared flag. The relay stops draining and outbound HTTP
        // is physically blocked while the toggle is on.
        var airGapState = new AirGapState();
        var relayService = new OutboxRelayService(syncOutboxRepository, syncTransport, healthReportWriter, airGapState);

        // ── Whoop integration ─────────────────────────────────────────────────
        // Credentials come from the WHOOP_API_CLIENT_ID / WHOOP_API_SECRET env vars,
        // optionally seeded from a gitignored .env file found alongside the app/repo.
        DotEnvFile.Load(FindDotEnv());
        var whoopClientId = Environment.GetEnvironmentVariable("WHOOP_API_CLIENT_ID") ?? "";
        var whoopSecret = Environment.GetEnvironmentVariable("WHOOP_API_SECRET") ?? "";

        var whoopHttp = new HttpClient(
            new AirGapHttpHandler(airGapState) { InnerHandler = new HttpClientHandler() });
        var tokenStore = new EncryptedFileOAuthTokenStore(vault, Path.Combine(dataDirectory, "tokens"));
        var whoopOptions = new WhoopDriverOptions { ClientId = whoopClientId, ClientSecret = whoopSecret };
        var whoopAuthenticator = new WhoopAuthenticator(
            whoopOptions, whoopHttp, tokenStore, loggerFactory.CreateLogger<WhoopAuthenticator>());
        var whoopDriver = new WhoopDriver(
            tokenStore, whoopHttp, whoopOptions, whoopAuthenticator, loggerFactory.CreateLogger<WhoopDriver>());
        var ingestionOrchestrator = new IngestionOrchestrator(
            biometricRepository, inferenceService, loggerFactory.CreateLogger<IngestionOrchestrator>());
        var whoopCoordinator = new WhoopSyncCoordinator(
            whoopAuthenticator, whoopDriver, ingestionOrchestrator, tokenStore,
            isConfigured: !string.IsNullOrWhiteSpace(whoopClientId) && !string.IsNullOrWhiteSpace(whoopSecret));

        var dashboardFacade = new DashboardDataFacade(
            biometricRepository,
            inferenceService,
            new RawChartSeriesStrategy(TimeSpan.FromHours(24), threshold: 1440),
            new StreamedChartSeriesStrategy(TimeSpan.FromDays(30), threshold: 2048),
            new AggregateChartSeriesStrategy(threshold: 2048));

        var analysisFacade = new AnalysisLabFacade(
            biometricRepository,
            new IntradayAnalysisBucketStrategy(TimeSpan.FromDays(7)),
            new DailyAnalysisBucketStrategy(TimeSpan.FromDays(45), 60 * 60 * 6, "6-hour buckets"),
            new DailyAnalysisBucketStrategy(TimeSpan.MaxValue, 60 * 60 * 24, "1-day buckets"));

        var importCoordinator = new DataImportCoordinator(biometricRepository);

        // Dev-only demo data: opt in with AXON_SEED_DEMO_DATA=1. Never on by default,
        // since seeded rows go through the real ingest path (and the sync outbox).
        // Runs to completion before any view model reads the vault;
        // a fire-and-forget seed would race the dashboard's first queries.
        if (TelemetrySeedDataService.IsEnabled(
                Environment.GetEnvironmentVariable(TelemetrySeedDataService.EnableVariable)))
        {
            var seedService = new TelemetrySeedDataService(biometricRepository);
            Task.Run(() => seedService.SeedIfEmptyAsync().AsTask()).GetAwaiter().GetResult();
        }

        // License tier: from a validated AXON_LICENSE_KEY, else a dev default.
        // PRODUCTION must default to LicenseTier.Free once Store billing is wired.
        var licenseKey = Environment.GetEnvironmentVariable("AXON_LICENSE_KEY");
        var licenseTier = !string.IsNullOrEmpty(licenseKey) && LicenseKey.TryValidate(licenseKey, out var t)
            ? t
            : LicenseTier.Pro;
        var licenseContext = new LicenseContext(licenseTier);

        var tagRepository = new TagRepository(db);

        // "Erase all data" in Settings. Before the keys are destroyed, nothing may still be
        // reading the database, holding the field key or writing the log file.
        var wipe = new DataWipeService(dataDirectory, vault,
        [
            new("Stopping the sync relay", () => relayService.DisposeAsync()),
            new("Clearing the field-encryption key from memory", () => encryptedRepository.DisposeAsync()),
            new("Closing the database", () =>
            {
                db.Close();
                return ValueTask.CompletedTask;
            }),
            new("Closing the log file", () =>
            {
                loggerFactory.Dispose();
                return ValueTask.CompletedTask;
            }),
        ]);

        var dashboard = new DashboardViewModel(dashboardFacade);
        var analysisLab = new AnalysisLabViewModel(analysisFacade, tagRepository, biometricRepository);
        var mainWindow = new MainWindowViewModel(
            dashboard, analysisLab, relayService, observabilityRuntime, whoopCoordinator,
            airGapState, importCoordinator, licenseContext, wipe);

        // Data-residency proof for the Settings privacy panel.
        mainWindow.Settings.DataFolderPath = dataDirectory;
        mainWindow.Settings.IsHardwareBacked = vault.IsHardwareBacked;
        mainWindow.Settings.VaultType = vault.IsHardwareBacked
            ? "DPAPI / TPM (Windows)"
            : "Mock (Dev — not hardware-backed)";
        mainWindow.Settings.DataFootprintText = DescribeFootprint(dataDirectory);
        mainWindow.Settings.LicenseTierText = licenseTier.ToString();

        return new AppRuntime(
            mainWindow, relayService, loggerFactory, observabilityRuntime,
            whoopHttp, encryptedRepository);
    }

    /// <summary>
    /// Locates a <c>.env</c> file by walking up from the app base directory
    /// (so a repo-root <c>.env</c> is found during <c>dotnet run</c>). Returns a
    /// best-effort path; <see cref="DotEnvFile.Load"/> no-ops if it does not exist.
    /// </summary>
    private static string FindDotEnv()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, ".env");
    }

    /// <summary>Sums the on-disk size of all local data into a human-readable string.</summary>
    private static string DescribeFootprint(string dataDirectory)
    {
        try
        {
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories))
                bytes += new FileInfo(file).Length;

            string[] units = ["B", "KB", "MB", "GB"];
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return $"{size:0.#} {units[unit]} on this machine";
        }
        catch
        {
            return "stored on this machine";
        }
    }
}

internal sealed class AppRuntime(
    MainWindowViewModel mainWindowViewModel,
    IAsyncDisposable relayService,
    ILoggerFactory loggerFactory,
    IDisposable observabilityRuntime,
    IDisposable whoopHttp,
    IAsyncDisposable repository) : IDisposable
{
    public MainWindowViewModel MainWindowViewModel { get; } = mainWindowViewModel;

    public void Dispose()
    {
        relayService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // Zero the cached field-encryption key held by the EncryptionDecorator.
        repository.DisposeAsync().AsTask().GetAwaiter().GetResult();
        whoopHttp.Dispose();
        // Flush and close Serilog sinks (writes any buffered log entries).
        loggerFactory.Dispose();
        observabilityRuntime.Dispose();
    }
}
