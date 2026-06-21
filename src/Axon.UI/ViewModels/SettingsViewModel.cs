using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.UI.Commands;

namespace Axon.UI.ViewModels;

/// <summary>
/// ViewModel for the "Sovereign Settings" panel.
///
/// Surfaces:
///   • Encryption status and key fingerprint (last 8 hex chars of SHA-256(key))
///   • Hardware vault type (TPM, Secure Enclave, Mock)
///   • Sync log (last N outbox events)
///   • GDPR "Nuclear Option" — triggers IHardwareVault.DestroyKeyAsync
///
/// The Nuclear Option handler is intentionally NOT wired here as a simple
/// ICommand — it requires a separate confirmation ViewModel with a typed
/// passphrase challenge. The <c>NuclearOptionRequested</c> event bubbles up
/// to the shell for that multi-step flow.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<bool>? AirGapChanged;
    public event EventHandler<bool>? TelemetryEnabledChanged;

    /// <summary>Raised when the user initiates the GDPR key-destruction flow.</summary>
    public event EventHandler? NuclearOptionRequested;

    /// <summary>Raised when the user clicks "Connect Whoop" (interactive OAuth).</summary>
    public event EventHandler? WhoopConnectRequested;

    /// <summary>Raised when the user clicks "Sync now" for Whoop.</summary>
    public event EventHandler? WhoopSyncRequested;

    /// <summary>Raised when the user clicks "Open data folder".</summary>
    public event EventHandler? OpenDataFolderRequested;

    private readonly DelegateCommand _connectWhoopCommand;
    private readonly DelegateCommand _syncWhoopCommand;

    public SettingsViewModel()
    {
        _connectWhoopCommand = new DelegateCommand(
            () => WhoopConnectRequested?.Invoke(this, EventArgs.Empty),
            () => IsWhoopConfigured && !IsWhoopBusy);
        _syncWhoopCommand = new DelegateCommand(
            () => WhoopSyncRequested?.Invoke(this, EventArgs.Empty),
            () => IsWhoopConfigured && !IsWhoopBusy);
        OpenDataFolderCommand = new DelegateCommand(
            () => OpenDataFolderRequested?.Invoke(this, EventArgs.Empty));
    }

    // ── Data residency (privacy proof) ────────────────────────────────────────

    private string _dataFolderPath = "";
    /// <summary>Absolute path to the local data directory — shown so users can verify it.</summary>
    public string DataFolderPath
    {
        get => _dataFolderPath;
        set => SetField(ref _dataFolderPath, value);
    }

    private string _dataFootprintText = "—";
    /// <summary>Human-readable size of all local data (e.g. "47.2 MB on this machine").</summary>
    public string DataFootprintText
    {
        get => _dataFootprintText;
        set => SetField(ref _dataFootprintText, value);
    }

    /// <summary>Opens the local data folder in the OS file browser.</summary>
    public ICommand OpenDataFolderCommand { get; }

    /// <summary>
    /// Set by the shell — performs the actual import + persistence + dashboard refresh.
    /// The View supplies the chosen file paths (it owns the file-picker dialog).
    /// </summary>
    public Func<IReadOnlyList<string>, Task>? ImportHandler { get; set; }

    private string _importStatusText = "Import CSV data from any source.";
    public string ImportStatusText
    {
        get => _importStatusText;
        set => SetField(ref _importStatusText, value);
    }

    /// <summary>Invoked by the View after the user picks files in the OS dialog.</summary>
    public async Task RunImportAsync(IReadOnlyList<string> paths)
    {
        if (ImportHandler is null || paths.Count == 0) return;
        await ImportHandler(paths).ConfigureAwait(true);
    }

    // ── Vault info ────────────────────────────────────────────────────────────

    private bool _isHardwareBacked;
    public bool IsHardwareBacked
    {
        get => _isHardwareBacked;
        set => SetField(ref _isHardwareBacked, value);
    }

    private string _licenseTierText = "Free";
    /// <summary>Current license tier (Free / Pro / Lifetime).</summary>
    public string LicenseTierText
    {
        get => _licenseTierText;
        set => SetField(ref _licenseTierText, value);
    }

    private string _vaultType = "Unknown";
    /// <summary>Display name: "TPM 2.0", "Secure Enclave", "Mock (Dev)" etc.</summary>
    public string VaultType
    {
        get => _vaultType;
        set => SetField(ref _vaultType, value);
    }

    private string _keyFingerprint = "--------";
    /// <summary>Last 8 hex chars of SHA-256(master key) — safe to display publicly.</summary>
    public string KeyFingerprint
    {
        get => _keyFingerprint;
        set => SetField(ref _keyFingerprint, value);
    }

    // ── Sync telemetry ────────────────────────────────────────────────────────

    private int _pendingOutboxItems;
    public int PendingOutboxItems
    {
        get => _pendingOutboxItems;
        set => SetField(ref _pendingOutboxItems, value);
    }

    private DateTimeOffset? _lastSuccessfulSync;
    public DateTimeOffset? LastSuccessfulSync
    {
        get => _lastSuccessfulSync;
        set => SetField(ref _lastSuccessfulSync, value);
    }

    private string _syncTransportName = "Unknown";
    public string SyncTransportName
    {
        get => _syncTransportName;
        set => SetField(ref _syncTransportName, value);
    }

    private string _syncStatusText = "Idle";
    public string SyncStatusText
    {
        get => _syncStatusText;
        set => SetField(ref _syncStatusText, value);
    }

    private string? _lastSyncError;
    public string? LastSyncError
    {
        get => _lastSyncError;
        set => SetField(ref _lastSyncError, value);
    }

    // ── Air-gap ───────────────────────────────────────────────────────────────

    private bool _airGapEnabled;
    public bool AirGapEnabled
    {
        get => _airGapEnabled;
        set
        {
            if (!SetField(ref _airGapEnabled, value)) return;
            AirGapChanged?.Invoke(this, value);
        }
    }

    private bool _telemetryEnabled;
    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set
        {
            if (!SetField(ref _telemetryEnabled, value)) return;
            TelemetryEnabledChanged?.Invoke(this, value);
        }
    }

    // ── Whoop connection ──────────────────────────────────────────────────────

    private bool _isWhoopConfigured;
    /// <summary>True when Whoop API credentials are present; gates the Connect/Sync buttons.</summary>
    public bool IsWhoopConfigured
    {
        get => _isWhoopConfigured;
        set
        {
            if (!SetField(ref _isWhoopConfigured, value)) return;
            RaiseWhoopCommandState();
        }
    }

    private bool _isWhoopBusy;
    /// <summary>True while a connect/sync operation is running; disables the buttons.</summary>
    public bool IsWhoopBusy
    {
        get => _isWhoopBusy;
        set
        {
            if (!SetField(ref _isWhoopBusy, value)) return;
            RaiseWhoopCommandState();
        }
    }

    private string _whoopStatusText = "Not connected";
    /// <summary>Human-readable Whoop connection/sync status for the Settings panel.</summary>
    public string WhoopStatusText
    {
        get => _whoopStatusText;
        set => SetField(ref _whoopStatusText, value);
    }

    public ICommand ConnectWhoopCommand => _connectWhoopCommand;

    public ICommand SyncWhoopCommand => _syncWhoopCommand;

    private void RaiseWhoopCommandState()
    {
        _connectWhoopCommand.RaiseCanExecuteChanged();
        _syncWhoopCommand.RaiseCanExecuteChanged();
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    public void RequestNuclearOption() =>
        NuclearOptionRequested?.Invoke(this, EventArgs.Empty);

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
