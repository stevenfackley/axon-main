using Axon.Core.Ports;

namespace Axon.UI.Application;

internal sealed record DashboardSnapshot(
    double HeartRate,
    double HeartRateVariability,
    double SpO2,
    double RecoveryScore,
    double ReadinessScore,
    ChartSeriesResult ChartSeries,
    IReadOnlyList<AnomalyResult> Anomalies,
    IReadOnlyList<ForecastPoint> RecoveryForecast);
