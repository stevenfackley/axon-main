using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Analytics;

namespace Axon.UI.Application;

internal sealed class DashboardDataFacade : IDashboardDataFacade
{
    private static readonly TimeSpan ForecastHistoryWindow = TimeSpan.FromDays(60);
    private static readonly TimeSpan AnomalyWindowCap = TimeSpan.FromDays(14);

    private readonly IBiometricRepository _repository;
    private readonly IInferenceService _inferenceService;
    private readonly IChartSeriesStrategy[] _chartSeriesStrategies;

    public DashboardDataFacade(
        IBiometricRepository repository,
        IInferenceService inferenceService,
        params IChartSeriesStrategy[] chartSeriesStrategies)
    {
        _repository = repository;
        _inferenceService = inferenceService;
        _chartSeriesStrategies = chartSeriesStrategies;
    }

    public async ValueTask<DashboardSnapshot> LoadAsync(
        BiometricType activeMetric,
        DateTimeOffset viewportStart,
        DateTimeOffset viewportEnd,
        CancellationToken ct = default)
    {
        var span = viewportEnd - viewportStart;
        var chartStrategy = ResolveChartStrategy(span);

        var latestVitalsTask = _repository.GetLatestVitalsAsync(ct).AsTask();
        var chartTask = chartStrategy.LoadAsync(_repository, activeMetric, viewportStart, viewportEnd, ct).AsTask();

        var anomalyStart = viewportEnd - (span <= AnomalyWindowCap ? span : AnomalyWindowCap);
        var hrTask = _repository.QueryRangeAsync(BiometricType.HeartRate, anomalyStart, viewportEnd, ct).AsTask();
        var hrvTask = _repository.QueryRangeAsync(BiometricType.HeartRateVariability, anomalyStart, viewportEnd, ct).AsTask();

        var forecastStart = viewportEnd - ForecastHistoryWindow;
        var sleepTask = _repository.QueryRangeAsync(BiometricType.SleepEfficiency, forecastStart, viewportEnd, ct).AsTask();
        var strainTask = _repository.QueryRangeAsync(BiometricType.StrainScore, forecastStart, viewportEnd, ct).AsTask();

        await Task.WhenAll(latestVitalsTask, chartTask, hrTask, hrvTask, sleepTask, strainTask);

        var anomaliesTask = _inferenceService.DetectAnomaliesAsync(hrTask.Result, hrvTask.Result, ct).AsTask();
        var forecastTask = _inferenceService.ForecastRecoveryAsync(sleepTask.Result, strainTask.Result, 7, ct).AsTask();

        await Task.WhenAll(anomaliesTask, forecastTask);

        var latestVitals = latestVitalsTask.Result;
        var trainingLoad = ComputeTrainingLoad(strainTask.Result);
        return new DashboardSnapshot(
            HeartRate: GetLatestValue(latestVitals, BiometricType.HeartRate),
            HeartRateVariability: GetLatestValue(latestVitals, BiometricType.HeartRateVariability),
            SpO2: GetLatestValue(latestVitals, BiometricType.SpO2),
            RecoveryScore: GetLatestValue(latestVitals, BiometricType.RecoveryScore),
            ReadinessScore: GetLatestValue(latestVitals, BiometricType.ReadinessScore),
            ChartSeries: chartTask.Result,
            Anomalies: anomaliesTask.Result.Where(a => a.IsAnomaly).ToArray(),
            RecoveryForecast: forecastTask.Result,
            TrainingLoadCtl: trainingLoad.Ctl,
            TrainingLoadAtl: trainingLoad.Atl,
            TrainingLoadTsb: trainingLoad.Tsb);
    }

    /// <summary>
    /// Derives the latest CTL/ATL/TSB from the strain history: one load value per
    /// calendar day (peak strain), oldest-first, fed to the EWMA calculator.
    /// </summary>
    private static (double Ctl, double Atl, double Tsb) ComputeTrainingLoad(
        IReadOnlyList<BiometricEvent> strain)
    {
        if (strain.Count == 0) return (0d, 0d, 0d);

        var dailyLoads = strain
            .GroupBy(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Max(e => e.Value)))
            .ToList();

        var series = TrainingLoadCalculator.Calculate(dailyLoads);
        var latest = series[^1];
        return (latest.Ctl, latest.Atl, latest.Tsb);
    }

    private IChartSeriesStrategy ResolveChartStrategy(TimeSpan span)
    {
        foreach (var strategy in _chartSeriesStrategies)
        {
            if (strategy.CanHandle(span))
            {
                return strategy;
            }
        }

        throw new InvalidOperationException("No chart strategy registered for the requested viewport span.");
    }

    private static double GetLatestValue(
        IReadOnlyDictionary<BiometricType, BiometricEvent> latestVitals,
        BiometricType type) =>
        latestVitals.TryGetValue(type, out var evt) ? evt.Value : 0d;
}
