namespace Axon.Core.Licensing;

/// <summary>
/// Represents the subscription tier active for the current Axon installation.
/// </summary>
public enum LicenseTier
{
    /// <summary>Manual-import only, 1-year history, single source, basic visualization.</summary>
    Free,

    /// <summary>Full feature set: API sync, unlimited history, ML engine, sovereign sync.</summary>
    Pro,

    /// <summary>Identical to <see cref="Pro"/> but perpetual (time-unlimited).</summary>
    Lifetime,
}
