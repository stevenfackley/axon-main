using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.UI.Application;
using Axon.UI.Commands;

namespace Axon.UI.ViewModels;

/// <summary>
/// ViewModel for the "Sovereign Settings" panel.
///
/// Surfaces:
///   • Encryption status and key fingerprint (last 8 hex chars of SHA-256(key))
///   • Hardware vault type (TPM, Secure Enclave, Mock)
///   • Sync log (last N outbox events)
///   • "Erase all data": armed only once the person types <see cref="EraseConfirmationWord"/>;
///     the shell supplies <see cref="EraseHandler"/>, which runs the DataWipeService.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<bool>? TelemetryEnabledChanged;

    /// <summary>Raised when the person clicks "Close Axon" after a complete erase.</summary>
    public event EventHandler? CloseAppRequested;

    /// <summary>Raised when the user clicks "Connect Whoop" (interactive OAuth).</summary>
    public event EventHandler? WhoopConnectRequested;

    /// <summary>Raised when the user clicks "Sync now" for Whoop.</summary>
    public event EventHandler? WhoopSyncRequested;

    /// <summary>Raised when the user clicks "Open data folder".</summary>
    public event EventHandler? OpenDataFolderRequested;

    // ── About / What's New (rec #9 — in-app changelog + methodology) ──────────

    /// <summary>App version string shown in the About card.</summary>
    public string AppVersion => "Axon 0.1.0 — Alpha";

    /// <summary>Recent notable changes — the in-app changelog (trust/freshness signal).</summary>
    public IReadOnlyList<string> ChangelogEntries { get; } =
    [
        "Live Whoop OAuth sync with encrypted, local-only token storage.",
        "CSV import for data from any source.",
        "Local AI insights: anomaly explanations, recovery outlook, CTL/ATL/TSB training load.",
        "Real air-gap mode — outbound network blocked at the HTTP layer.",
        "Hardware-backed (DPAPI) encryption with mandatory audit + field encryption.",
        "Shareable insight cards and a first-run onboarding flow.",
    ];

    /// <summary>Plain-language methodology / provenance statement for skeptical users.</summary>
    public string InsightMethodology =>
        "All analysis runs locally via ML.NET — your biometric data is never sent anywhere. " +
        "Each insight shows its sample size and confidence so you can judge it, not just trust it. " +
        "Anomalies are measured against your personal baseline; the recovery outlook blends sleep " +
        "efficiency and strain; training load uses the standard CTL/ATL/TSB exponentially-weighted averages.";

    private readonly DelegateCommand _connectWhoopCommand;
    private readonly DelegateCommand _syncWhoopCommand;
    private readonly AsyncCommand _eraseAllDataCommand;
    private readonly DelegateCommand _closeAppCommand;
    private readonly AirGapState _airGapState;
    private bool _isImporting;

    public SettingsViewModel(AirGapState airGapState)
    {
        _airGapState = airGapState;
        _airGapState.Changed += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AirGapEnabled)));
        _connectWhoopCommand = new DelegateCommand(
            () => WhoopConnectRequested?.Invoke(this, EventArgs.Empty),
            () => IsWhoopConfigured && !IsWhoopBusy && !IsDataErased);
        _syncWhoopCommand = new DelegateCommand(
            () => WhoopSyncRequested?.Invoke(this, EventArgs.Empty),
            () => IsWhoopConfigured && !IsWhoopBusy && !IsDataErased);
        OpenDataFolderCommand = new DelegateCommand(
            () => OpenDataFolderRequested?.Invoke(this, EventArgs.Empty));
        _eraseAllDataCommand = new AsyncCommand(EraseAllDataAsync, CanEraseAllData);
        _closeAppCommand = new DelegateCommand(
            () => CloseAppRequested?.Invoke(this, EventArgs.Empty),
            () => IsEraseComplete);
    }

    // ── Data residency (privacy proof) ────────────────────────────────────────

    private string _dataFolderPath = "";
    /// <summary>Absolute path to the local data directory — shown so users can verify it.</summary>
    public string DataFolderPath
    {
        get => _dataFolderPath;
        set
        {
            if (!SetField(ref _dataFolderPath, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EraseDescription)));
        }
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
        if (ImportHandler is null || paths.Count == 0 || IsDataErased) return;
        _isImporting = true;
        try
        {
            await ImportHandler(paths).ConfigureAwait(true);
        }
        finally
        {
            _isImporting = false;
        }
    }

    // ── Vault info ────────────────────────────────────────────────────────────

    private bool _isHardwareBacked;
    public bool IsHardwareBacked
    {
        get => _isHardwareBacked;
        set
        {
            if (!SetField(ref _isHardwareBacked, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EraseDescription)));
        }
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

    /// <summary>Air-Gap Mode toggle, backed by the shared <see cref="AirGapState"/>.</summary>
    public bool AirGapEnabled
    {
        get => _airGapState.Enabled;
        set => _airGapState.Enabled = value;
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

    // ── Erase all data ────────────────────────────────────────────────────────

    /// <summary>The word the person types to arm "Erase all data".</summary>
    public const string EraseConfirmationWord = "ERASE";

    /// <summary>
    /// Set by the shell: stops the app's use of the data and runs the erase. The result says
    /// what was and was not erased.
    /// </summary>
    internal Func<Task<DataWipeResult>>? EraseHandler
    {
        get => _eraseHandler;
        set
        {
            _eraseHandler = value;
            _eraseAllDataCommand.RaiseCanExecuteChanged();
        }
    }

    private Func<Task<DataWipeResult>>? _eraseHandler;

    /// <summary>What the erase does, exactly. Shown on the card.</summary>
    public string EraseDescription =>
        $"Permanently erases the files Axon itself keeps in {DataFolderPath}: the database with your " +
        "readings, tags and tag notes, the sync outbox and the audit log; the encryption keys; the Whoop " +
        "sign-in stored here; Axon's logs and its health report. Anything else in that folder is left " +
        "where it is. " +
        (IsHardwareBacked
            ? "The keys are destroyed first, so a database file that cannot be deleted can no longer be decrypted. "
            : "") +
        "This cannot be undone. Axon does not send your readings off this device, so there is no copy " +
        "elsewhere for it to erase. Your Whoop account and the data Whoop holds are not affected; to " +
        "withdraw Axon's access to your Whoop account, revoke it in your Whoop account settings. " +
        "Afterwards Axon has to be closed; it starts empty next time.";

    private string _eraseConfirmationText = "";
    /// <summary>What the person typed into the confirmation box.</summary>
    public string EraseConfirmationText
    {
        get => _eraseConfirmationText;
        set
        {
            if (!SetField(ref _eraseConfirmationText, value ?? "")) return;
            _eraseAllDataCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isDataErased;
    /// <summary>
    /// True once an erase has started. From then on the app reads no data: Settings shows
    /// only the erase card, and the rest of the app stays closed.
    /// </summary>
    public bool IsDataErased
    {
        get => _isDataErased;
        private set
        {
            if (!SetField(ref _isDataErased, value)) return;
            RaiseWhoopCommandState();
        }
    }

    private bool _isEraseComplete;
    /// <summary>True when the last erase left nothing behind.</summary>
    public bool IsEraseComplete
    {
        get => _isEraseComplete;
        private set
        {
            if (!SetField(ref _isEraseComplete, value)) return;
            _closeAppCommand.RaiseCanExecuteChanged();
        }
    }

    private string _eraseStatusText = "";
    /// <summary>Progress, then the result: what was erased and what is still there.</summary>
    public string EraseStatusText
    {
        get => _eraseStatusText;
        private set => SetField(ref _eraseStatusText, value);
    }

    public ICommand EraseAllDataCommand => _eraseAllDataCommand;

    public ICommand CloseAppCommand => _closeAppCommand;

    private bool CanEraseAllData() =>
        EraseHandler is not null
        && !IsEraseComplete
        && string.Equals(EraseConfirmationText, EraseConfirmationWord, StringComparison.Ordinal);

    private async Task EraseAllDataAsync()
    {
        if (EraseHandler is null) return;
        if (IsWhoopBusy || _isImporting)
        {
            EraseStatusText = "A Whoop sync or an import is still running. Nothing was erased. " +
                              "Wait for it to finish, then erase again.";
            return;
        }

        IsDataErased = true;
        EraseStatusText = "Erasing…";
        try
        {
            var result = await EraseHandler().ConfigureAwait(true);
            if (result.Outcome == DataWipeOutcome.Refused)
                IsDataErased = false;   // nothing was touched: the app carries on
            IsEraseComplete = result.Complete;
            EraseStatusText = result.Complete
                ? result.Describe() + "\n\nClose Axon to finish. It starts empty next time."
                : result.Describe();
        }
        catch (Exception ex)
        {
            // The service reports failures in its result, so this is a bug; never imply success.
            EraseStatusText = $"The erase stopped unexpectedly ({ex.Message}). Some data may still be " +
                              "on this device. Erase again.";
        }
        finally
        {
            _eraseAllDataCommand.RaiseCanExecuteChanged();
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
