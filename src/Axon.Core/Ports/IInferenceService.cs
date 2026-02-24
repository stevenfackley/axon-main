using Axon.Core.Domain;

namespace Axon.Core.Ports;

/// <summary>
/// Port for on-device ML inference operations.
///
/// All methods are asynchronous and non-blocking — implementations MUST
/// execute model inference on a background thread so the UI thread is
/// never stalled. The 120fps render loop depends on this contract.
///
/// AOT Safety: Implementations must load models via <see cref="FileStream"/>
/// and use explicitly typed data views; no reflection-based schema discovery.
/// </summary>
public interface IInferenceService
{
    /// <summary>
    /// Runs the IID Spike Detection pipeline over a window of Heart Rate and
    /// HRV samples. Returns one <see cref="AnomalyResult"/> per input point;
    /// points with <see cref="AnomalyResult.IsAnomaly"/> == true should be
    /// flagged on the telemetry chart.
    /// </summary>
    /// <param name="heartRateSamples">
    ///     Chronologically ordered HR samples (BiometricType.HeartRate, bpm).
    /// </param>
    /// <param name="hrvSamples">
    ///     Chronologically ordered HRV samples (BiometricType.HeartRateVariability, ms).
    /// </param>
    /// <param name="ct">Propagated cancellation token.</param>
    ValueTask<IReadOnlyList<AnomalyResult>> DetectAnomaliesAsync(
        IReadOnlyList<BiometricEvent> heartRateSamples,
        IReadOnlyList<BiometricEvent> hrvSamples,
        CancellationToken             ct = default);

    /// <summary>
    /// Uses the Recovery Forecaster (SSA TimeSeries) to predict the next
    /// <paramref name="horizonDays"/> days of readiness scores based on the
    /// supplied historical sleep and strain events.
    /// </summary>
    /// <param name="sleepHistory">Historical SleepDuration / SleepEfficiency events.</param>
    /// <param name="strainHistory">Historical StrainScore events.</param>
    /// <param name="horizonDays">Number of future days to forecast (default: 7).</param>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <returns>
    ///     One <see cref="ForecastPoint"/> per forecast day, with predicted
    ///     readiness score and 95% prediction interval bounds.
    /// </returns>
    ValueTask<IReadOnlyList<ForecastPoint>> ForecastRecoveryAsync(
        IReadOnlyList<BiometricEvent> sleepHistory,
        IReadOnlyList<BiometricEvent> strainHistory,
        int                           horizonDays = 7,
        CancellationToken             ct          = default);
}

/// <summary>
/// Result of a single IID spike-detection evaluation for one data point.
/// </summary>
/// <param name="Timestamp">The timestamp of the evaluated data point.</param>
/// <param name="BiometricType">The ACS metric type that was evaluated.</param>
/// <param name="IsAnomaly">True when the model flags this point as a spike/dip anomaly.</param>
/// <param name="Score">Raw anomaly score from the model (higher = more anomalous).</param>
/// <param name="PValue">
///     Statistical p-value for the spike. Values below 0.05 are conventionally
///     flagged as significant anomalies.
/// </param>
public sealed record AnomalyResult(
    DateTimeOffset Timestamp,
    BiometricType  BiometricType,
    bool           IsAnomaly,
    double         Score,
    double         PValue)
{
    /// <summary>PII Shield: suppress score in logs.</summary>
    public override string ToString() =>
        $"AnomalyResult {{ Type={BiometricType}, Timestamp={Timestamp:O}, IsAnomaly={IsAnomaly} }}";
}

/// <summary>
/// A single forecast data point returned by the Recovery Forecaster.
/// </summary>
/// <param name="Date">The calendar date this prediction covers (UTC, time component = midnight).</param>
/// <param name="PredictedReadiness">Model point-estimate for the readiness score (0–100).</param>
/// <param name="LowerBound">Lower bound of the 95% prediction interval.</param>
/// <param name="UpperBound">Upper bound of the 95% prediction interval.</param>
public sealed record ForecastPoint(
    DateTimeOffset Date,
    float          PredictedReadiness,
    float          LowerBound,
    float          UpperBound);
