using System.Net.Http;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
        _runtime = BuildRuntime();
        var vm = _runtime.MainWindowViewModel;

        switch (ApplicationLifetime)
        {
            // Desktop (Windows / Linux / macOS)
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = vm };
                desktop.MainWindow.Opened += async (_, _) => await vm.InitializeAsync();
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

    private static AppRuntime BuildRuntime()
    {
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Axon");
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

        // Real hardware-backed key vault on Windows (DPAPI/TPM); dev mock elsewhere.
        // NOTE: switching vaults changes key derivation — a database created under the
        // mock vault cannot be opened under DPAPI. Delete %LOCALAPPDATA%/Axon to reset.
        var keysDir = Path.Combine(dataDirectory, "keys");
        Directory.CreateDirectory(keysDir);
        IHardwareVault vault = OperatingSystem.IsWindows()
            ? new WindowsDataProtectionVault(keysDir)
            : new MockHardwareVault();

        var dbFactory = new AxonDbContextFactory(vault);
        var db = dbFactory.CreateAsync(dataDirectory).AsTask().GetAwaiter().GetResult();

        // Mandatory decorator chain (outer → inner): Audit → Encryption → concrete repo.
        // Every biometric write is AES-256-GCM field-encrypted and audit-logged — no bypass.
        var encryptedRepository = new EncryptionDecorator(new BiometricRepository(db), vault);
        IBiometricRepository biometricRepository = new AuditLoggingDecorator(
            encryptedRepository, new DbAuditLogger(db), callerIdentity: Environment.UserName);
        var syncOutboxRepository = new SyncOutboxRepository(db);
        var inferenceService = new LocalInferenceService(loggerFactory.CreateLogger<LocalInferenceService>());
        var syncTransport = new LoopbackSyncTransport();
        var relayService = new OutboxRelayService(syncOutboxRepository, syncTransport, healthReportWriter);

        // ── Whoop integration ─────────────────────────────────────────────────
        // Credentials come from the WHOOP_API_CLIENT_ID / WHOOP_API_SECRET env vars,
        // optionally seeded from a gitignored .env file found alongside the app/repo.
        DotEnvFile.Load(FindDotEnv());
        var whoopClientId = Environment.GetEnvironmentVariable("WHOOP_API_CLIENT_ID") ?? "";
        var whoopSecret = Environment.GetEnvironmentVariable("WHOOP_API_SECRET") ?? "";

        var whoopHttp = new HttpClient();
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

        var dashboard = new DashboardViewModel(dashboardFacade);
        var analysisLab = new AnalysisLabViewModel(analysisFacade);
        var mainWindow = new MainWindowViewModel(
            dashboard, analysisLab, relayService, observabilityRuntime, whoopCoordinator);

        return new AppRuntime(
            mainWindow, inferenceService, db, relayService, loggerFactory, observabilityRuntime,
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
}

internal sealed class AppRuntime(
    MainWindowViewModel mainWindowViewModel,
    IDisposable inferenceService,
    IDisposable dbContext,
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
        inferenceService.Dispose();
        dbContext.Dispose();
        whoopHttp.Dispose();
        // Flush and close Serilog sinks (writes any buffered log entries).
        loggerFactory.Dispose();
        observabilityRuntime.Dispose();
    }
}
