namespace Axon.UI.Application;

/// <summary>
/// Shared, thread-safe toggle for "Air-Gap Mode". When enabled, the app must not
/// perform any outbound network I/O. This is the single owner of the flag: the
/// HTTP pipeline and the outbox relay read it, and the Settings and shell view
/// models write it and listen to <see cref="Changed"/>, so the guarantee is
/// enforced in one place rather than asserted in marketing copy.
/// </summary>
public sealed class AirGapState
{
    private volatile bool _enabled;

    /// <summary>
    /// Raised after <see cref="Enabled"/> changes value, on the thread that changed it.
    /// Setting the current value again raises nothing.
    /// </summary>
    public event EventHandler<bool>? Changed;

    /// <summary>True when outbound networking is disabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Changed?.Invoke(this, value);
        }
    }
}
