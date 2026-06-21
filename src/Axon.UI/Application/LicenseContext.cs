using Axon.Core.Licensing;

namespace Axon.UI.Application;

/// <summary>
/// Holds the resolved <see cref="LicenseTier"/> for the running app and answers
/// feature-gating questions. A single instance is shared so every entry point
/// gates consistently.
/// </summary>
public sealed class LicenseContext(LicenseTier tier)
{
    public LicenseTier Tier { get; } = tier;

    /// <summary>True when <paramref name="feature"/> is included in the current tier.</summary>
    public bool IsAllowed(Feature feature) => FeatureGate.IsAllowed(feature, Tier);
}
