namespace Axon.Infrastructure.ML;

/// <summary>
/// Flat input schema for the ML.NET IID Spike Detector pipeline.
///
/// AOT Safety: Explicitly typed; no reflection-based column discovery.
/// The column name "Value" is bound by string literal in the estimator
/// definition rather than attribute scanning.
/// </summary>
internal sealed class BiometricInputRow
{
    /// <summary>
    /// The raw floating-point measurement value (e.g., bpm, ms).
    /// This is the only feature column consumed by the spike detector.
    /// </summary>
    public float Value { get; set; }
}

/// <summary>
/// Output schema produced by the IidSpikeEstimator.
/// Column "Prediction" maps to [Alert, Score, P-Value] from ML.NET.
/// </summary>
internal sealed class SpikeOutputRow
{
    /// <summary>
    /// Three-element vector: [alert (0|1), raw score, p-value].
    /// Populated by the IidSpikeEstimator output column "Prediction".
    /// </summary>
    public float[]? Prediction { get; set; }
}

/// <summary>
/// Flat input schema for the SSA forecasting pipeline.
/// One row = one day of aggregated recovery features.
/// </summary>
internal sealed class RecoveryInputRow
{
    /// <summary>Aggregated daily readiness proxy (0–100 scale).</summary>
    public float ReadinessProxy { get; set; }
}

/// <summary>Output schema produced by the SsaForecastingEstimator.</summary>
internal sealed class RecoveryForecastRow
{
    /// <summary>
    /// Forecast vector: [point estimates] length == horizonDays.
    /// Column name "Forecast" bound in the estimator definition.
    /// </summary>
    public float[]? Forecast { get; set; }

    /// <summary>Lower confidence bounds (95%); length == horizonDays.</summary>
    public float[]? ConfidenceLowerBound { get; set; }

    /// <summary>Upper confidence bounds (95%); length == horizonDays.</summary>
    public float[]? ConfidenceUpperBound { get; set; }
}
