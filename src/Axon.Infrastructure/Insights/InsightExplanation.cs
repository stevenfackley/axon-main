namespace Axon.Infrastructure.Insights;

/// <summary>
/// A natural-language insight card produced by <see cref="InsightExplanationService"/>.
///
/// Immutable DTO — safe to cache and bind directly to UI view-models.
///
/// PII safety: none of the string fields ever contain raw biometric values;
/// the service is responsible for ensuring this invariant.
/// </summary>
/// <param name="Title">Short headline for the insight card (≤ 60 characters).</param>
/// <param name="Detail">
///     One-to-two sentence body copy that explains the finding in plain English.
/// </param>
/// <param name="Confidence">
///     Human-readable confidence descriptor derived from sample size,
///     e.g. "High (n=90)", "Moderate (n=30)", or "Low — needs more data (n=8)".
/// </param>
/// <param name="SampleSize">
///     Raw sample count used to produce this explanation; retained for
///     view-model display and downstream telemetry.
/// </param>
public sealed record InsightExplanation(
    string Title,
    string Detail,
    string Confidence,
    int SampleSize);
