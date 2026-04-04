using System.ComponentModel;
using System.Runtime.CompilerServices;
using Axon.Core.Ports;

namespace Axon.UI.ViewModels;

/// <summary>
/// Root ViewModel for the Axon shell window.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ISyncOutboxRepository _syncOutboxRepository;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        ISyncOutboxRepository syncOutboxRepository)
    {
        Dashboard = dashboard;
        _syncOutboxRepository = syncOutboxRepository;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private int _activeTabIndex;
    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set => SetField(ref _activeTabIndex, value);
    }

    private bool _airGapEnabled;
    public bool AirGapEnabled
    {
        get => _airGapEnabled;
        set => SetField(ref _airGapEnabled, value);
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

    public SettingsViewModel Settings { get; } = new();

    public async Task InitializeAsync()
    {
        await Dashboard.InitializeAsync();
        await RefreshShellStateAsync();
    }

    public async Task RefreshShellStateAsync(CancellationToken ct = default)
    {
        PendingOutboxCount = await _syncOutboxRepository.CountPendingAsync(ct);
        SyncStatus = AirGapEnabled ? SyncStatus.AirGapped : SyncStatus.Idle;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum SyncStatus : byte
{
    Idle = 0,
    Syncing = 1,
    Error = 2,
    AirGapped = 3
}
