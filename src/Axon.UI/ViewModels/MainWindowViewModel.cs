using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Axon.UI.ViewModels;

/// <summary>
/// Root ViewModel for the Axon shell window.
///
/// Owns the active page/tab and provides top-level commands that cross
/// ViewModel boundaries (e.g. Air-Gap toggle, global sync state indicator).
///
/// Follows strict MVVM: no direct dependency on Avalonia UI types, making it
/// unit-testable without a UI host. All bindings are driven by
/// <see cref="INotifyPropertyChanged"/>.
///
/// Navigation model: Axon uses a "tab-per-module" layout.
///   • Tab 0 → Dashboard (vitals overview)
///   • Tab 1 → Analysis Lab (ML.NET correlation workspace)
///   • Tab 2 → Sovereign Settings (encryption, sync, key management)
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Active tab ────────────────────────────────────────────────────────────

    private int _activeTabIndex;
    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set => SetField(ref _activeTabIndex, value);
    }

    // ── Air-Gap mode ──────────────────────────────────────────────────────────

    private bool _airGapEnabled;
    /// <summary>
    /// When true, all outbound network I/O is blocked (gRPC sync, API polling).
    /// The value is persisted to the encrypted settings store on change.
    /// </summary>
    public bool AirGapEnabled
    {
        get => _airGapEnabled;
        set => SetField(ref _airGapEnabled, value);
    }

    // ── Sync state indicator ──────────────────────────────────────────────────

    private SyncStatus _syncStatus = SyncStatus.Idle;
    public SyncStatus SyncStatus
    {
        get => _syncStatus;
        set => SetField(ref _syncStatus, value);
    }

    private int _pendingOutboxCount;
    /// <summary>Number of unprocessed <c>SyncOutbox</c> entries.</summary>
    public int PendingOutboxCount
    {
        get => _pendingOutboxCount;
        set => SetField(ref _pendingOutboxCount, value);
    }

    // ── Child ViewModels ──────────────────────────────────────────────────────

    public DashboardViewModel   Dashboard   { get; } = new();
    public SettingsViewModel    Settings    { get; } = new();

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Top-level sync status shown in the status bar.</summary>
public enum SyncStatus : byte
{
    Idle        = 0,
    Syncing     = 1,
    Error       = 2,
    AirGapped   = 3
}
