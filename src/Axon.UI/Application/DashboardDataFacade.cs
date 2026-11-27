using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Analytics;
using Axon.Infrastructure.Persistence;

namespace Axon.UI.Application;

internal sealed class DashboardDataFacade : IDashboardDataFacade
{
    private static readonly TimeSpan ForecastHistoryWindow = TimeSpan.FromDays(60);

    /// <summary>
    /// The spike detector is fitted on this window, ending at the newest heart-rate or HRV
    /// reading whatever the viewport shows, so a flag does not appear or vanish as the person
    /// pans. The detector's baseline is at most the window's first half, so at least its last
    /// 14 days are always scored.
    /// </summary>
    private static readonly TimeSpan AnomalyScoringWindow = TimeSpan.FromDays(28);

    private readonly IBiometricRepository _repository;
    private readonly IInferenceService _inferenceService;
    private readonly VaultActivity? _activity;
    private readonly IChartSeriesStrategy[] _chartSeriesStrategies;

    /// <param name="activity">When set, each load is tracked so the data erase can stop it.</param>
    public DashboardDataFacade(
        IBiometricRepository repository,
        IInferenceService inferenceService,
        VaultActivity? activity,
        params IChartSeriesStrategy[] chartSeriesStrategies)
    {
        _repository = repository;
        _inferenceService = inferenceService;
        _activity = activity;
        _chartSeriesStrategies = chartSeriesStrategies;
    }

    public async ValueTask<DashboardSnapshot> LoadAsync(
        BiometricType activeMetric,
        DateTimeOffset viewportStart,
        DateTimeOffset viewportEnd,
        CancellationToken ct = default)
    {
        using var operation = _activity?.TryBegin("the dashboard load", ct);
        if (_activity is not null && operation is null)
            throw new OperationCanceledException("Axon's data is being erased.");
        ct = operation?.Token ?? ct;

        var span = viewportEnd - viewportStart;
        var chartStrategy = ResolveChartStrategy(span);

        var latestVitalsTask = _repository.GetLatestVitalsAsync(ct).AsTask();
        var chartTask = chartStrategy.LoadAsync(_repository, activeMetric, viewportStart, viewportEnd, ct).AsTask();

        var forecastStart = viewportEnd - ForecastHistoryWindow;
        var sleepTask = _repository.QueryRangeAsync(BiometricType.SleepEfficiency, forecastStart, viewportEnd, ct).AsTask();
        var strainTask = _repository.QueryRangeAsync(BiometricType.StrainScore, forecastStart, viewportEnd, ct).AsTask();

        var anomalyEnd = NewestReading(await latestVitalsTask) ?? viewportEnd;
        var anomalyStart = anomalyEnd - AnomalyScoringWindow;
        var hrTask = _repository.QueryRangeAsync(BiometricType.HeartRate, anomalyStart, anomalyEnd, ct).AsTask();
        var hrvTask = _repository.QueryRangeAsync(BiometricType.HeartRateVariability, anomalyStart, anomalyEnd, ct).AsTask();

        await Task.WhenAll(chartTask, hrTask, hrvTask, sleepTask, strainTask);

        var anomaliesTask = RunModelAsync(
            () => _inferenceService.DetectAnomaliesAsync(hrTask.Result, hrvTask.Result, ct), ct);
        var forecastTask = RunModelAsync(
            () => _inferenceService.ForecastRecoveryAsync(sleepTask.Result, strainTask.Result, 7, ct), ct);

        await Task.WhenAll(anomaliesTask, forecastTask);

        var latestVitals = latestVitalsTask.Result;
        var trainingLoad = ComputeTrainingLoad(strainTask.Result);
        var (scored, anomalyError) = anomaliesTask.Result;
        // Flags and counts are limited to what the viewport shows.
        var anomalyResults = scored.Where(a => a.Timestamp >= viewportStart && a.Timestamp <= viewportEnd).ToArray();
        var (forecast, forecastError) = forecastTask.Result;
        return new DashboardSnapshot(
            HeartRate: GetLatestValue(latestVitals, BiometricType.HeartRate),
            HeartRateVariability: GetLatestValue(latestVitals, BiometricType.HeartRateVariability),
            SpO2: GetLatestValue(latestVitals, BiometricType.SpO2),
            RecoveryScore: GetLatestValue(latestVitals, BiometricType.RecoveryScore),
            ReadinessScore: GetLatestValue(latestVitals, BiometricType.ReadinessScore),
            ChartSeries: chartTask.Result,
            ScoredAnomalies: scored,
            AnomalyResults: anomalyResults,
            Anomalies: anomalyResults.Where(a => a.IsAnomaly).ToArray(),
            AnomalyModelError: anomalyError,
            RecoveryForecast: forecast,
            ForecastHistoryDays: CountDaysWithData(sleepTask.Result, strainTask.Result),
            ForecastModelError: forecastError,
            TrainingLoadCtl: trainingLoad.Ctl,
            TrainingLoadAtl: trainingLoad.Atl,
            TrainingLoadTsb: trainingLoad.Tsb);
    }

    /// <summary>
    /// Runs one model. A failure leaves that model's result empty with the reason, so the
    /// vitals, chart and the other model still load; cancellation still propagates.
    /// </summary>
    private static async Task<(IReadOnlyList<T> Result, string? Error)> RunModelAsync<T>(
        Func<ValueTask<IReadOnlyList<T>>> run,
        CancellationToken ct)
    {
        try
        {
            return (await run(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (Array.Empty<T>(), ex.Message);
        }
    }

    private static DateTimeOffset? NewestReading(IReadOnlyDictionary<BiometricType, BiometricEvent> latest)
    {
        DateTimeOffset? newest = null;
        foreach (var type in (ReadOnlySpan<BiometricType>)[BiometricType.HeartRate, BiometricType.HeartRateVariability])
        {
            if (latest.TryGetValue(type, out var evt) && (newest is null || evt.Timestamp > newest))
                newest = evt.Timestamp;
        }

        return newest;
    }

    /// <summary>UTC calendar days with at least one finite sleep or strain reading.</summary>
    private static int CountDaysWithData(
        IReadOnlyList<BiometricEvent> sleep,
        IReadOnlyList<BiometricEvent> strain) =>
        sleep.Concat(strain)
            .Where(e => double.IsFinite(e.Value))
            .Select(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime))
            .Distinct()
            .Count();

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
