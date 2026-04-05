using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.Core.Ports;
using Axon.UI.Commands;

namespace Axon.UI.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ISyncOutboxRepository _syncOutboxRepository;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        AnalysisLabViewModel analysisLab,
        ISyncOutboxRepository syncOutboxRepository)
    {
        Dashboard = dashboard;
        AnalysisLab = analysisLab;
        _syncOutboxRepository = syncOutboxRepository;

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
            Settings.AirGapEnabled = value;
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

    public DashboardViewModel Dashboard { get; }

    public AnalysisLabViewModel AnalysisLab { get; }

    public SettingsViewModel Settings { get; } = new();

    public async Task InitializeAsync()
    {
        Settings.VaultType = "Mock (Dev)";
        Settings.IsHardwareBacked = false;
        Settings.KeyFingerprint = "SEED-AXON";
        await Dashboard.InitializeAsync();
        await AnalysisLab.InitializeAsync();
        await RefreshShellStateAsync();
    }

    public async Task RefreshShellStateAsync(CancellationToken ct = default)
    {
        PendingOutboxCount = await _syncOutboxRepository.CountPendingAsync(ct);
        Settings.PendingOutboxItems = PendingOutboxCount;
        SyncStatus = AirGapEnabled ? SyncStatus.AirGapped : SyncStatus.Idle;
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
