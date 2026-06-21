using Axon.Core.Ports;

namespace Axon.Infrastructure.Insights;

/// <summary>
/// Translates raw ML inference output (anomaly detection, recovery forecasting)
/// into human-readable insight cards with plain-English phrasing.
///
/// Design constraints:
///   • 100% pure — inputs in, records out; no I/O, no side effects.
///   • Deterministic — identical inputs always produce identical outputs.
///   • AOT-safe — no reflection; record construction only.
///   • PII-safe — no biometric values ever appear in the returned strings.
/// </summary>
public sealed class InsightExplanationService
{
    // ── Confidence thresholds ─────────────────────────────────────────────────

    /// <summary>Minimum sample count to reach "High" confidence.</summary>
    private const int HighConfidenceMinSamples = 60;

    /// <summary>Minimum sample count to reach "Moderate" confidence.</summary>
    private const int ModerateConfidenceMinSamples = 21;

    /// <summary>
    /// Sample count below which all explanations carry a "needs more data" caveat.
    /// Mirrors LocalInferenceService.MinSpikeDetectorSamples.
    /// </summary>
    private const int LowSampleThreshold = 12;

    // ── Anomaly explanation ───────────────────────────────────────────────────

    /// <summary>
    /// Produces a single <see cref="InsightExplanation"/> summarising a collection
    /// of <see cref="AnomalyResult"/> records (typically one metric's window).
    /// </summary>
    /// <param name="results">Anomaly results for the window. May be empty.</param>
    /// <param name="baselineDays">
    ///     Number of calendar days represented by <paramref name="results"/>; used
    ///     for confidence labelling and the baseline description in copy.
    /// </param>
    /// <returns>
    ///     A human-readable explanation. When <paramref name="results"/> is empty or
    ///     below the minimum sample threshold the explanation explicitly states that
    ///     more data is required.
    /// </returns>
    public InsightExplanation ExplainAnomalies(
        IReadOnlyList<AnomalyResult> results,
        int baselineDays)
    {
        int n = results.Count;

        if (n == 0)
            return InsufficientDataExplanation(
                "Anomaly Detection",
                baselineDays,
                "No data points were available to evaluate.");

        if (n < LowSampleThreshold)
            return InsufficientDataExplanation(
                "Anomaly Detection",
                baselineDays,
                $"Only {n} data point{(n == 1 ? "" : "s")} recorded — at least {LowSampleThreshold} are needed for reliable detection.");

        // Count flagged anomalies.
        int anomalyCount = 0;
        double worstPValue = 1.0;
        foreach (var r in results)
        {
            if (r.IsAnomaly)
            {
                anomalyCount++;
                if (r.PValue < worstPValue)
                    worstPValue = r.PValue;
            }
        }

        string confidence = BuildConfidence(n);

        if (anomalyCount == 0)
        {
            return new InsightExplanation(
                Title: "All Clear",
                Detail: $"No anomalies detected across {n} data point{(n == 1 ? "" : "s")} in your {baselineDays}-day baseline. Readings are within expected range.",
                Confidence: confidence,
                SampleSize: n);
        }

        // Determine severity from worst p-value.
        string severity = worstPValue < 0.01
            ? "high-severity"
            : worstPValue < 0.03
                ? "moderate"
                : "mild";

        double pct = Math.Round(100.0 * anomalyCount / n, 1);

        string detail = severity switch
        {
            "high-severity" =>
                $"{anomalyCount} out of {n} reading{(anomalyCount == 1 ? "" : "s")} ({pct}%) triggered a high-severity alert against your {baselineDays}-day baseline. This is a statistically significant deviation — consider reviewing recent activity or consulting a professional.",
            "moderate" =>
                $"{anomalyCount} out of {n} reading{(anomalyCount == 1 ? "" : "s")} ({pct}%) showed a moderate anomaly compared to your {baselineDays}-day baseline. Watch for a pattern over the next few days.",
            _ =>
                $"{anomalyCount} out of {n} reading{(anomalyCount == 1 ? "" : "s")} ({pct}%) showed a mild deviation from your {baselineDays}-day baseline. This is likely within normal daily variation."
        };

        string title = severity switch
        {
            "high-severity" => "High-Severity Anomaly Detected",
            "moderate" => "Moderate Anomaly Detected",
            _ => "Mild Deviation Noted"
        };

        return new InsightExplanation(
            Title: title,
            Detail: detail,
            Confidence: confidence,
            SampleSize: n);
    }

    /// <summary>
    /// Produces a single <see cref="InsightExplanation"/> for a HRV anomaly window,
    /// using percentile-style phrasing ("bottom X% of your baseline").
    /// </summary>
    /// <param name="results">HRV anomaly results.</param>
    /// <param name="baselineDays">Days of history represented by the window.</param>
    public InsightExplanation ExplainHrvAnomalies(
        IReadOnlyList<AnomalyResult> results,
        int baselineDays)
    {
        int n = results.Count;

        if (n == 0)
            return InsufficientDataExplanation(
                "HRV Analysis",
                baselineDays,
                "No HRV data points were available.");

        if (n < LowSampleThreshold)
            return InsufficientDataExplanation(
                "HRV Analysis",
                baselineDays,
                $"Only {n} HRV reading{(n == 1 ? "" : "s")} recorded — at least {LowSampleThreshold} are needed.");

        int anomalyCount = 0;
        foreach (var r in results)
            if (r.IsAnomaly) anomalyCount++;

        string confidence = BuildConfidence(n);

        if (anomalyCount == 0)
        {
            return new InsightExplanation(
                Title: "HRV Within Baseline",
                Detail: $"Your HRV is tracking consistently within your {baselineDays}-day baseline across {n} reading{(n == 1 ? "" : "s")}.",
                Confidence: confidence,
                SampleSize: n);
        }

        // Express anomaly prevalence as a bottom-percentile estimate.
        int pct = (int)Math.Ceiling(100.0 * anomalyCount / n);

        string severity = pct >= 20 ? "significantly" : pct >= 10 ? "notably" : "slightly";

        return new InsightExplanation(
            Title: "HRV Deviation Detected",
            Detail: $"Your HRV is in the bottom {pct}% of your {baselineDays}-day baseline — {severity} below your typical range across {anomalyCount} of {n} reading{(n == 1 ? "" : "s")}. Prioritise recovery today.",
            Confidence: confidence,
            SampleSize: n);
    }

    // ── Forecast explanation ──────────────────────────────────────────────────

    /// <summary>
    /// Produces a single <see cref="InsightExplanation"/> from a recovery forecast
    /// horizon, identifying the next predicted low-recovery day and summarising
    /// the overall readiness band.
    /// </summary>
    /// <param name="forecast">
    ///     Ordered list of <see cref="ForecastPoint"/> values (typically 7 days).
    ///     May be empty when insufficient history is available.
    /// </param>
    /// <param name="trainingDays">
    ///     Number of days of history used to train the model; drives confidence
    ///     label and low-sample caveat.
    /// </param>
    /// <param name="lowReadinessThreshold">
    ///     Score at or below which a day is considered a "low recovery" day.
    ///     Defaults to 40 (bottom 40% of the 0–100 scale).
    /// </param>
    public InsightExplanation ExplainForecast(
        IReadOnlyList<ForecastPoint> forecast,
        int trainingDays,
        float lowReadinessThreshold = 40f)
    {
        if (forecast.Count == 0)
            return InsufficientDataExplanation(
                "Recovery Forecast",
                trainingDays,
                "Not enough history to generate a forecast. Keep logging data and check back in a few days.");

        string confidence = BuildConfidence(trainingDays);

        // Find the next low-recovery day.
        ForecastPoint? nextLow = null;
        foreach (var pt in forecast)
        {
            if (pt.PredictedReadiness <= lowReadinessThreshold)
            {
                nextLow = pt;
                break;
            }
        }

        // Compute average predicted readiness across the window.
        float sum = 0f;
        foreach (var pt in forecast)
            sum += pt.PredictedReadiness;
        float avgReadiness = sum / forecast.Count;

        string band = avgReadiness >= 70f ? "high" : avgReadiness >= 50f ? "moderate" : "low";

        string lowDayText = nextLow is null
            ? $"No low-recovery days are forecast over the next {forecast.Count} day{(forecast.Count == 1 ? "" : "s")}."
            : $"If recent patterns hold, your next likely low-recovery day is {nextLow.Date:dddd, MMMM d}.";

        string detail = $"{lowDayText} Overall readiness is projected to be {band} (average {avgReadiness:F0}/100) across the {forecast.Count}-day window.";

        if (trainingDays < LowSampleThreshold)
            detail += " Note: confidence is limited — more data will improve accuracy.";

        return new InsightExplanation(
            Title: "Recovery Outlook",
            Detail: detail,
            Confidence: confidence,
            SampleSize: trainingDays);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a human-readable confidence descriptor from the sample size.
    /// Thresholds: High ≥ 60, Moderate ≥ 21, Low &lt; 21.
    /// </summary>
    public static string BuildConfidence(int sampleSize) =>
        sampleSize >= HighConfidenceMinSamples
            ? $"High (n={sampleSize})"
            : sampleSize >= ModerateConfidenceMinSamples
                ? $"Moderate (n={sampleSize})"
                : $"Low — needs more data (n={sampleSize})";

    private static InsightExplanation InsufficientDataExplanation(
        string domain,
        int baselineDays,
        string reason) =>
        new(
            Title: $"{domain} — Insufficient Data",
            Detail: reason + (baselineDays > 0
                ? $" ({baselineDays} day{(baselineDays == 1 ? "" : "s")} of history available.)"
                : ""),
            Confidence: BuildConfidence(0),
            SampleSize: 0);
}
