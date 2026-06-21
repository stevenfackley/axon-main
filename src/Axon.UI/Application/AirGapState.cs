namespace Axon.UI.Application;

/// <summary>
/// Shared, thread-safe toggle for "Air-Gap Mode". When enabled, the app must not
/// perform any outbound network I/O. A single instance is shared between the
/// Settings toggle, the outbox relay, and the HTTP pipeline so the guarantee is
/// enforced in one place rather than asserted in marketing copy.
/// </summary>
public sealed class AirGapState
{
    private volatile bool _enabled;

    /// <summary>True when outbound networking is disabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}
