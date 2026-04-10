using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Axon.Infrastructure.ML;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;
using Axon.UI.Application;
using Axon.UI.ViewModels;
using Axon.UI.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
#if !ANDROID && !IOS
using Axon.UI.Logging;
using Axon.UI.Observability;
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

        var vault = new MockHardwareVault();
        var dbFactory = new AxonDbContextFactory(vault);
        var db = dbFactory.CreateAsync(dataDirectory).AsTask().GetAwaiter().GetResult();

        var biometricRepository = new BiometricRepository(db);
        var syncOutboxRepository = new SyncOutboxRepository(db);
        var inferenceService = new LocalInferenceService(loggerFactory.CreateLogger<LocalInferenceService>());
        var syncTransport = new LoopbackSyncTransport();
        var relayService = new OutboxRelayService(syncOutboxRepository, syncTransport, healthReportWriter);

        var seedDataService = new TelemetrySeedDataService(biometricRepository);
        seedDataService.EnsureSeedDataAsync().AsTask().GetAwaiter().GetResult();

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
        var mainWindow = new MainWindowViewModel(dashboard, analysisLab, relayService, observabilityRuntime);

        return new AppRuntime(mainWindow, inferenceService, db, relayService, loggerFactory, observabilityRuntime);
    }
}

internal sealed class AppRuntime(
    MainWindowViewModel mainWindowViewModel,
    IDisposable inferenceService,
    IDisposable dbContext,
    IAsyncDisposable relayService,
    ILoggerFactory loggerFactory,
    IDisposable observabilityRuntime) : IDisposable
{
    public MainWindowViewModel MainWindowViewModel { get; } = mainWindowViewModel;

    public void Dispose()
    {
        relayService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        inferenceService.Dispose();
        dbContext.Dispose();
        // Flush and close Serilog sinks (writes any buffered log entries).
        loggerFactory.Dispose();
        observabilityRuntime.Dispose();
    }
}
