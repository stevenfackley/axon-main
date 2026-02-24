using System.ComponentModel;
using System.Runtime.CompilerServices;

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

    /// <summary>Raised when the user initiates the GDPR key-destruction flow.</summary>
    public event EventHandler? NuclearOptionRequested;

    // ── Vault info ────────────────────────────────────────────────────────────

    private bool _isHardwareBacked;
    public bool IsHardwareBacked
    {
        get => _isHardwareBacked;
        set => SetField(ref _isHardwareBacked, value);
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

    // ── Air-gap ───────────────────────────────────────────────────────────────

    private bool _airGapEnabled;
    public bool AirGapEnabled
    {
        get => _airGapEnabled;
        set => SetField(ref _airGapEnabled, value);
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    public void RequestNuclearOption() =>
        NuclearOptionRequested?.Invoke(this, EventArgs.Empty);

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
