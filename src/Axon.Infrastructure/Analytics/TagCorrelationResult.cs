namespace Axon.Infrastructure.Analytics;

/// <summary>
/// The association result between one <see cref="Axon.Core.Domain.Tag"/> and a biometric
/// daily series, as computed by <see cref="TagCorrelationAnalyzer"/>.
/// </summary>
/// <param name="TagName">Display name of the tag.</param>
/// <param name="MeanWith">Mean biometric value on days when the tag was applied.</param>
/// <param name="MeanWithout">Mean biometric value on days when the tag was absent.</param>
/// <param name="EffectSize">
///     Signed mean difference: <c>MeanWith − MeanWithout</c>.
///     Positive ⟹ tag correlates with higher metric; negative ⟹ lower metric.
/// </param>
/// <param name="Coefficient">
///     Point-biserial correlation coefficient in [−1, 1].
///     Zero when variance is absent or sample size is too small.
/// </param>
/// <param name="SampleSize">Total number of day-observations used.</param>
/// <param name="Strength">
///     Human-readable label: "Strong", "Moderate", "Weak", "Negligible",
///     or "Needs more data" when <c>SampleSize</c> is below the guard threshold.
/// </param>
public sealed record TagCorrelationResult(
    string TagName,
    double MeanWith,
    double MeanWithout,
    double EffectSize,
    double Coefficient,
    int SampleSize,
    string Strength);
