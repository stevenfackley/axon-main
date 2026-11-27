using Axon.Core.Ports;

namespace Axon.UI.Application;

/// <param name="AnomalyResults">Every point the spike detector scored (empty when it failed).</param>
/// <param name="Anomalies">The flagged subset of <paramref name="AnomalyResults"/>, for the chart markers.</param>
/// <param name="AnomalyModelError">Why the spike detector failed, or null when it ran.</param>
/// <param name="ForecastHistoryDays">Days with sleep or strain data that fed the forecast.</param>
/// <param name="ForecastModelError">Why the forecaster failed, or null when it ran.</param>
internal sealed record DashboardSnapshot(
    double HeartRate,
    double HeartRateVariability,
    double SpO2,
    double RecoveryScore,
    double ReadinessScore,
    ChartSeriesResult ChartSeries,
    IReadOnlyList<AnomalyResult> AnomalyResults,
    IReadOnlyList<AnomalyResult> Anomalies,
    string? AnomalyModelError,
    IReadOnlyList<ForecastPoint> RecoveryForecast,
    int ForecastHistoryDays,
    string? ForecastModelError,
    double TrainingLoadCtl,
    double TrainingLoadAtl,
    double TrainingLoadTsb);
