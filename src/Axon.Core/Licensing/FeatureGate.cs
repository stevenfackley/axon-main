namespace Axon.Core.Licensing;

/// <summary>
/// Pure, stateless feature-gate that encodes the PRD §6 entitlement matrix.
/// </summary>
/// <remarks>
/// Call sites check access before exposing any gated functionality:
/// <code>
/// if (!FeatureGate.IsAllowed(Feature.CorrelationLab, currentTier))
///     throw new EntitlementException(Feature.CorrelationLab);
/// </code>
/// The current <see cref="LicenseTier"/> should be obtained from
/// <see cref="LicenseKey.TryValidate"/> at application startup and cached
/// in a single <c>ILicenseContext</c> service registered in the DI container.
/// </remarks>
public static class FeatureGate
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="feature"/> is accessible
    /// under <paramref name="tier"/>; <see langword="false"/> otherwise.
    /// </summary>
    /// <param name="feature">The feature being requested.</param>
    /// <param name="tier">The active subscription tier.</param>
    public static bool IsAllowed(Feature feature, LicenseTier tier) =>
        feature switch
        {
            // Free features are available to everyone.
            Feature.ManualImport        => true,
            Feature.BasicVisualization  => true,
            Feature.TwelveMonthHistory  => true,

            // Pro / Lifetime exclusive features.
            Feature.ApiSync             => tier is LicenseTier.Pro or LicenseTier.Lifetime,
            Feature.UnlimitedHistory    => tier is LicenseTier.Pro or LicenseTier.Lifetime,
            Feature.MlInsightEngine     => tier is LicenseTier.Pro or LicenseTier.Lifetime,
            Feature.CorrelationLab      => tier is LicenseTier.Pro or LicenseTier.Lifetime,
            Feature.ShareableExports    => tier is LicenseTier.Pro or LicenseTier.Lifetime,
            Feature.MultiSource         => tier is LicenseTier.Pro or LicenseTier.Lifetime,
            Feature.SovereignSync       => tier is LicenseTier.Pro or LicenseTier.Lifetime,

            // Unknown future features: deny by default (fail-closed).
            _ => false,
        };
}
