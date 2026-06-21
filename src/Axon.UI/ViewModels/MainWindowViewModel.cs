using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.UI.Application;
using Axon.UI.Commands;
using Axon.UI.Observability;
using LicenseContext = Axon.UI.Application.LicenseContext;

namespace Axon.UI.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IOutboxRelayService _outboxRelayService;
    private readonly IObservabilityController _observabilityController;
    private readonly WhoopSyncCoordinator _whoop;
    private readonly AirGapState _airGapState;
    private readonly DataImportCoordinator _import;
    private readonly LicenseContext _license;

    internal MainWindowViewModel(
        DashboardViewModel dashboard,
        AnalysisLabViewModel analysisLab,
        IOutboxRelayService outboxRelayService,
        IObservabilityController observabilityController,
        WhoopSyncCoordinator whoop,
        AirGapState airGapState,
        DataImportCoordinator import,
        LicenseContext license)
    {
        Dashboard = dashboard;
        AnalysisLab = analysisLab;
        _outboxRelayService = outboxRelayService;
        _observabilityController = observabilityController;
        _whoop = whoop;
        _airGapState = airGapState;
        _import = import;
        _license = license;
        Settings.ImportHandler = ImportFilesAsync;
        _outboxRelayService.SnapshotChanged += OnRelaySnapshotChanged;

        Settings.AirGapChanged += OnAirGapChanged;
        Settings.TelemetryEnabledChanged += OnTelemetryEnabledChanged;
        Settings.TelemetryEnabled = _observabilityController.TelemetryEnabled;
        Settings.IsWhoopConfigured = _whoop.IsConfigured;
        Settings.WhoopConnectRequested += OnWhoopConnectRequested;
        Settings.WhoopSyncRequested += OnWhoopSyncRequested;
        Settings.OpenDataFolderRequested += OnOpenDataFolderRequested;

        ShowDashboardCommand = new DelegateCommand(() => ActiveWorkspace = ShellWorkspace.Dashboard);
        ShowAnalysisLabCommand = new DelegateCommand(() => ActiveWorkspace = ShellWorkspace.AnalysisLab);
        ShowSettingsCommand = new DelegateCommand(() => ActiveWorkspace = ShellWorkspace.Settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ShowDashboardCommand { get; }

    public ICommand ShowAnalysisLabCommand { get; }

    public ICommand ShowSettingsCommand { get; }

    private ShellWorkspace _activeWorkspace;
    public ShellWorkspace ActiveWorkspace
    {
        get => _activeWorkspace;
        set
        {
            if (!SetField(ref _activeWorkspace, value)) return;
            RaiseWorkspaceFlags();
        }
    }

    public bool IsDashboardActive => ActiveWorkspace == ShellWorkspace.Dashboard;

    public bool IsAnalysisLabActive => ActiveWorkspace == ShellWorkspace.AnalysisLab;

    public bool IsSettingsActive => ActiveWorkspace == ShellWorkspace.Settings;

    private bool _airGapEnabled;
    public bool AirGapEnabled
    {
        get => _airGapEnabled;
        set
        {
            if (!SetField(ref _airGapEnabled, value)) return;
            if (Settings.AirGapEnabled != value)
            {
                Settings.AirGapEnabled = value;
            }

            // Enforce at the HTTP boundary (blocks outbound) and on the sync relay.
            _airGapState.Enabled = value;
            _outboxRelayService.SetAirGapEnabled(value);
            SyncStatus = value ? SyncStatus.AirGapped : SyncStatus.Idle;
        }
    }

    private SyncStatus _syncStatus = SyncStatus.Idle;
    public SyncStatus SyncStatus
    {
        get => _syncStatus;
        set => SetField(ref _syncStatus, value);
    }

    private int _pendingOutboxCount;
    public int PendingOutboxCount
    {
        get => _pendingOutboxCount;
        set => SetField(ref _pendingOutboxCount, value);
    }

    private string _transportName = "Unknown";
    public string TransportName
    {
        get => _transportName;
        set => SetField(ref _transportName, value);
    }

    public DashboardViewModel Dashboard { get; }

    public AnalysisLabViewModel AnalysisLab { get; }

    public SettingsViewModel Settings { get; } = new();

    public async Task InitializeAsync()
    {
        // Vault type / footprint / data path are populated from the composition root.
        if (_whoop.IsConfigured && await _whoop.IsConnectedAsync())
            Settings.WhoopStatusText = "Connected";
        else if (!_whoop.IsConfigured)
            Settings.WhoopStatusText = "Not configured (set WHOOP_API_CLIENT_ID / WHOOP_API_SECRET)";

        await Dashboard.InitializeAsync();
        await AnalysisLab.InitializeAsync();
        await _outboxRelayService.StartAsync();
        await RefreshShellStateAsync();
    }

    public async Task RefreshShellStateAsync(CancellationToken ct = default)
    {
        await _outboxRelayService.RefreshAsync(ct);
        ApplyRelaySnapshot(_outboxRelayService.Current);
    }

    private void OnRelaySnapshotChanged(object? sender, RelaySnapshot snapshot) =>
        ApplyRelaySnapshot(snapshot);

    private void ApplyRelaySnapshot(RelaySnapshot snapshot)
    {
        PendingOutboxCount = snapshot.PendingCount;
        Settings.PendingOutboxItems = snapshot.PendingCount;
        Settings.LastSuccessfulSync = snapshot.LastSuccessfulSync;
        Settings.SyncTransportName = snapshot.TransportName;
        Settings.SyncStatusText = snapshot.State.ToString();
        Settings.LastSyncError = snapshot.LastError;
        TransportName = snapshot.TransportName;

        if (_airGapEnabled != snapshot.AirGapEnabled)
        {
            _airGapEnabled = snapshot.AirGapEnabled;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AirGapEnabled)));
            if (Settings.AirGapEnabled != snapshot.AirGapEnabled)
            {
                Settings.AirGapEnabled = snapshot.AirGapEnabled;
            }
        }

        SyncStatus = snapshot.State switch
        {
            RelayState.Syncing => SyncStatus.Syncing,
            RelayState.Error => SyncStatus.Error,
            RelayState.AirGapped => SyncStatus.AirGapped,
            _ => SyncStatus.Idle
        };
    }

    private void OnAirGapChanged(object? sender, bool enabled)
    {
        if (AirGapEnabled == enabled)
        {
            return;
        }

        AirGapEnabled = enabled;
    }

    private void OnTelemetryEnabledChanged(object? sender, bool enabled)
    {
        if (_observabilityController.TelemetryEnabled == enabled)
        {
            return;
        }

        _observabilityController.SetTelemetryEnabled(enabled);
    }

    private async void OnWhoopConnectRequested(object? sender, EventArgs e)
    {
        if (Settings.IsWhoopBusy) return;
        if (!_license.IsAllowed(Axon.Core.Licensing.Feature.ApiSync))
        {
            Settings.WhoopStatusText = "Automated Whoop sync is an Axon Pro feature — upgrade to enable it.";
            return;
        }
        Settings.IsWhoopBusy = true;
        Settings.WhoopStatusText = "Opening browser for Whoop consent…";
        try
        {
            await _whoop.ConnectAsync();
            Settings.WhoopStatusText = "Connected. Syncing…";
            await RunWhoopSyncAsync();
        }
        catch (Exception ex)
        {
            Settings.WhoopStatusText = $"Connection failed: {ex.Message}";
        }
        finally
        {
            Settings.IsWhoopBusy = false;
        }
    }

    private async void OnWhoopSyncRequested(object? sender, EventArgs e)
    {
        if (Settings.IsWhoopBusy) return;
        if (!_license.IsAllowed(Axon.Core.Licensing.Feature.ApiSync))
        {
            Settings.WhoopStatusText = "Automated Whoop sync is an Axon Pro feature — upgrade to enable it.";
            return;
        }
        Settings.IsWhoopBusy = true;
        try
        {
            await RunWhoopSyncAsync();
        }
        finally
        {
            Settings.IsWhoopBusy = false;
        }
    }

    private async Task ImportFilesAsync(IReadOnlyList<string> paths)
    {
        Settings.ImportStatusText = "Importing…";
        int total = 0;
        try
        {
            foreach (var path in paths)
                total += await _import.ImportCsvAsync(path);

            // Reload the dashboard so imported data appears immediately.
            await Dashboard.InitializeAsync();
            Settings.ImportStatusText = total > 0
                ? $"Imported {total} events. Dashboard updated."
                : "No valid rows found in the selected file(s).";
        }
        catch (Exception ex)
        {
            Settings.ImportStatusText = $"Import failed: {ex.Message}";
        }
    }

    private void OnOpenDataFolderRequested(object? sender, EventArgs e)
    {
        var path = Settings.DataFolderPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the file browser is best-effort; never crash the UI over it.
        }
    }

    private async Task RunWhoopSyncAsync()
    {
        Settings.WhoopStatusText = "Syncing Whoop data…";
        try
        {
            await _whoop.SyncNowAsync();
            // Reload the dashboard so freshly-ingested data appears immediately.
            await Dashboard.InitializeAsync();
            Settings.WhoopStatusText = $"Synced at {DateTimeOffset.Now:t}";
        }
        catch (Exception ex)
        {
            Settings.WhoopStatusText = $"Sync failed: {ex.Message}";
        }
    }

    private void RaiseWorkspaceFlags()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDashboardActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnalysisLabActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSettingsActive)));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public enum SyncStatus : byte
{
    Idle = 0,
    Syncing = 1,
    Error = 2,
    AirGapped = 3
}

public enum ShellWorkspace : byte
{
    Dashboard = 0,
    AnalysisLab = 1,
    Settings = 2
}
