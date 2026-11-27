using Axon.Core.Domain;
using Axon.Core.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;

namespace Axon.Infrastructure.ML;

/// <summary>
/// On-device ML inference engine using ML.NET.
///
/// Implements two models:
///   1. <b>IID Spike Detector</b> — detects sudden spikes/dips in Heart Rate
///      and HRV data using the IidSpikeEstimator (no stationarity assumption,
///      suitable for real-time wearable streams).
///   2. <b>Recovery Forecaster</b> — predicts future readiness scores using
///      Singular Spectrum Analysis (SSA) time-series forecasting.
///
/// Both are time-series transformers, which are not row-to-row mappers, so neither can
/// go through <c>CreatePredictionEngine</c>. The spike detector transforms the whole
/// window as one <see cref="IDataView"/>; the forecaster uses the time-series engine
/// (<c>CreateTimeSeriesEngine</c>) and its input-free <c>Predict()</c>.
///
/// State: every call builds its own <see cref="MLContext"/>, fits its model on the series
/// it was given and drops both afterwards. Nothing is cached or shared between calls, so
/// concurrent callers (the dashboard and the post-ingest pass) cannot see each other's
/// window and need no lock.
///
/// Threading: all model work runs on <c>Task.Run</c> to keep the UI thread free.
///
/// Native AOT: ML.NET builds its data views by reflection and generates code at run time,
/// so this class does not run in a Native AOT build.
/// </summary>
public sealed class LocalInferenceService : IInferenceService
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// IID Spike: confidence threshold above which a point is flagged.
    /// 0.95 → 95% confidence interval (p-value &lt; 0.05).
    /// </summary>
    private const double AnomalyConfidence = 0.95;

    /// <summary>
    /// Minimum number of finite samples required before the spike detector
    /// can produce a statistically meaningful result.
    /// </summary>
    private const int MinSpikeDetectorSamples = 12;

    /// <summary>Upper bound on the spike detector's p-value sliding window.</summary>
    private const int MaxPValueHistoryLength = 512;

    /// <summary>
    /// SSA window size: 7-day seasonality for recovery patterns.
    /// ML.NET requires the training series to be longer than twice this.
    /// </summary>
    private const int SsaWindowSize = 7;

    /// <summary>Fewest days of history the forecaster accepts: more than twice the window (15).</summary>
    private const int MinForecastHistoryDays = (SsaWindowSize * 2) + 1;

    /// <summary>
    /// Number of SSA eigentriples (rank of the decomposition).
    /// Higher = captures more complex seasonality; 4 is sufficient for daily readiness.
    /// </summary>
    private const int SsaRank = 4;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly ILogger<LocalInferenceService> _logger;

    // ── Constructor ───────────────────────────────────────────────────────────

    public LocalInferenceService(ILogger<LocalInferenceService> logger)
    {
        _logger = logger;
    }

    // ── IInferenceService ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<AnomalyResult>> DetectAnomaliesAsync(
        IReadOnlyList<BiometricEvent> heartRateSamples,
        IReadOnlyList<BiometricEvent> hrvSamples,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Run on background thread — never block the UI thread.
        return await Task.Run(() => RunSpikeDetection(heartRateSamples, hrvSamples, ct), ct)
                         .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ForecastPoint>> ForecastRecoveryAsync(
        IReadOnlyList<BiometricEvent> sleepHistory,
        IReadOnlyList<BiometricEvent> strainHistory,
        int horizonDays = 7,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(horizonDays, 1);
        ct.ThrowIfCancellationRequested();

        return await Task.Run(() => RunRecoveryForecast(sleepHistory, strainHistory, horizonDays, ct), ct)
                         .ConfigureAwait(false);
    }

    // ── Spike Detection (IID) ─────────────────────────────────────────────────

    private IReadOnlyList<AnomalyResult> RunSpikeDetection(
        IReadOnlyList<BiometricEvent> hrSamples,
        IReadOnlyList<BiometricEvent> hrvSamples,
        CancellationToken ct)
    {
        var results = new List<AnomalyResult>(hrSamples.Count + hrvSamples.Count);
        results.AddRange(DetectSpikes(hrSamples, BiometricType.HeartRate, ct));
        results.AddRange(DetectSpikes(hrvSamples, BiometricType.HeartRateVariability, ct));
        return results;
    }

    /// <summary>
    /// Fits a fresh IID spike detector on <paramref name="samples"/> and scores every finite
    /// sample in order. NaN and infinite readings are left out: one would poison the
    /// p-value history for the points after it. Returns only the points the detector could
    /// flag: the ones before its p-value window fills are its baseline and are dropped.
    /// </summary>
    private List<AnomalyResult> DetectSpikes(
        IReadOnlyList<BiometricEvent> samples,
        BiometricType metricType,
        CancellationToken ct)
    {
        var scored = new List<BiometricEvent>(samples.Count);
        foreach (var sample in samples)
        {
            if (float.IsFinite((float)sample.Value))
                scored.Add(sample);
        }

        if (scored.Count < MinSpikeDetectorSamples)
            return [];

        ct.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "[Inference] IID spike detection for {MetricType} over {Count} samples.",
            metricType, scored.Count);

        var mlContext = CreateContext();
        var dataView = mlContext.Data.LoadFromEnumerable(
            scored.Select(e => new BiometricInputRow { Value = (float)e.Value }));

        // pvalueHistoryLength: sliding window for p-value computation.
        // The estimator detects both tails (spikes AND dips) by default.
        int historyLength = Math.Min(scored.Count / 2, MaxPValueHistoryLength);
        var detector = mlContext.Transforms.DetectIidSpike(
            outputColumnName: nameof(SpikeOutputRow.Prediction),
            inputColumnName: nameof(BiometricInputRow.Value),
            confidence: AnomalyConfidence * 100.0,   // ML.NET uses 0–100 scale
            pvalueHistoryLength: historyLength);

        // The detector is stateful along the series: transforming the whole window scores
        // each row against the rows before it, in order. It raises no alert before row
        // historyLength - 1 (measured on 5.0.1), so those rows are baseline, not results.
        var transformed = detector.Fit(dataView).Transform(dataView);
        int firstFlaggable = historyLength - 1;

        var results = new List<AnomalyResult>(scored.Count - firstFlaggable);
        int index = 0;
        foreach (var row in mlContext.Data.CreateEnumerable<SpikeOutputRow>(transformed, reuseRowObject: false))
        {
            ct.ThrowIfCancellationRequested();
            if (index >= firstFlaggable)
                results.Add(MapSpikeResult(scored[index], metricType, row));
            index++;
        }

        return results;
    }

    private static AnomalyResult MapSpikeResult(
        BiometricEvent evt,
        BiometricType type,
        SpikeOutputRow output)
    {
        // ML.NET IidSpike Prediction = [alert, score, p-value]
        var pred = output.Prediction;
        bool isAlert = pred is { Length: >= 1 } && pred[0] > 0d;
        double score = pred is { Length: >= 2 } ? pred[1] : 0.0;
        double pValue = pred is { Length: >= 3 } ? pred[2] : 1.0;

        return new AnomalyResult(evt.Timestamp, type, isAlert, score, pValue);
    }

    // ── Recovery Forecasting (SSA) ────────────────────────────────────────────

    private IReadOnlyList<ForecastPoint> RunRecoveryForecast(
        IReadOnlyList<BiometricEvent> sleepHistory,
        IReadOnlyList<BiometricEvent> strainHistory,
        int horizonDays,
        CancellationToken ct)
    {
        // Compute daily readiness proxy: blend sleep efficiency and inverse strain.
        // Groups events by calendar day and produces a single float per day.
        var dailyReadiness = BuildDailyReadinessProxy(sleepHistory, strainHistory);

        if (dailyReadiness.Count < MinForecastHistoryDays)
        {
            _logger.LogWarning(
                "[Inference] Insufficient history ({Count} days) for SSA forecast; need {Min}.",
                dailyReadiness.Count, MinForecastHistoryDays);
            return Array.Empty<ForecastPoint>();
        }

        ct.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "[Inference] Fitting SSA Recovery Forecaster on {Days} days of history.",
            dailyReadiness.Count);

        // Fitted per call on the latest history; no model file is persisted.
        var mlContext = CreateContext();
        var rows = dailyReadiness.Values.Select(v => new RecoveryInputRow { ReadinessProxy = v });
        var dataView = mlContext.Data.LoadFromEnumerable(rows);

        // SSA Forecasting pipeline.
        // windowSize:          seasonality window (7 = weekly).
        // seriesLength:        training series length.
        // trainSize:           how many rows to use for training.
        // horizon:             forecast steps (days).
        // isAdaptive:          retrain incrementally as new data arrives.
        // discountFactor:      recency weighting for adaptive mode.
        // rankSelectionMethod: fixed rank (SsaRank).
        var pipeline = mlContext.Forecasting.ForecastBySsa(
            outputColumnName: nameof(RecoveryForecastRow.Forecast),
            inputColumnName: nameof(RecoveryInputRow.ReadinessProxy),
            windowSize: SsaWindowSize,
            seriesLength: dailyReadiness.Count,
            trainSize: dailyReadiness.Count,
            horizon: horizonDays,
            isAdaptive: true,
            discountFactor: 0.9f,
            rankSelectionMethod: RankSelectionMethod.Exact,
            rank: SsaRank,
            shouldStabilize: true,
            confidenceLevel: 0.95f,
            confidenceLowerBoundColumn: nameof(RecoveryForecastRow.ConfidenceLowerBound),
            confidenceUpperBoundColumn: nameof(RecoveryForecastRow.ConfidenceUpperBound));

        var model = pipeline.Fit(dataView);

        // Forecast-only: the input-free Predict() forecasts `horizon` steps on from the end of
        // the training series, without feeding it a new observation.
        using var engine = model.CreateTimeSeriesEngine<RecoveryInputRow, RecoveryForecastRow>(mlContext);
        var result = engine.Predict();
        var forecasts = result.Forecast ?? Array.Empty<float>();
        var lowers = result.ConfidenceLowerBound ?? Array.Empty<float>();
        var uppers = result.ConfidenceUpperBound ?? Array.Empty<float>();

        var forecastPoints = new ForecastPoint[forecasts.Length];
        var baseDate = DateTimeOffset.UtcNow.Date;

        for (int i = 0; i < forecasts.Length; i++)
        {
            // Clamp predictions to valid readiness range [0, 100].
            float predicted = Math.Clamp(forecasts[i], 0f, 100f);
            float lower = i < lowers.Length ? Math.Clamp(lowers[i], 0f, 100f) : predicted;
            float upper = i < uppers.Length ? Math.Clamp(uppers[i], 0f, 100f) : predicted;

            forecastPoints[i] = new ForecastPoint(
                Date: new DateTimeOffset(baseDate.AddDays(i + 1), TimeSpan.Zero),
                PredictedReadiness: predicted,
                LowerBound: lower,
                UpperBound: upper);
        }

        _logger.LogInformation(
            "[Inference] SSA Forecast complete: {Horizon} days predicted.",
            forecastPoints.Length);

        return forecastPoints;
    }

    /// <summary>
    /// Builds a day-keyed dictionary of readiness proxy values.
    /// Proxy = (sleep_efficiency_avg * 0.6) + ((100 - strain_avg) * 0.4).
    /// NaN and infinite readings are ignored; missing days are forward-filled from the
    /// last known value.
    /// </summary>
    private static SortedDictionary<DateOnly, float> BuildDailyReadinessProxy(
        IReadOnlyList<BiometricEvent> sleepHistory,
        IReadOnlyList<BiometricEvent> strainHistory)
    {
        var sleepByDay = DailyAverages(sleepHistory);
        var strainByDay = DailyAverages(strainHistory);

        // Union of all known days.
        var allDays = sleepByDay.Keys.Union(strainByDay.Keys).OrderBy(d => d).ToList();

        if (allDays.Count == 0)
            return new SortedDictionary<DateOnly, float>();

        var result = new SortedDictionary<DateOnly, float>();
        float lastSleep = 70f;   // Reasonable defaults if data is missing.
        float lastStrain = 50f;

        // Fill every calendar day from first to last (forward-fill gaps).
        var current = allDays[0];
        var last = allDays[^1];

        while (current <= last)
        {
            if (sleepByDay.TryGetValue(current, out var sleep)) lastSleep = sleep;
            if (strainByDay.TryGetValue(current, out var strain)) lastStrain = strain;

            // Readiness proxy: sleep efficiency weighted at 60%, recovery from strain at 40%.
            float proxy = (lastSleep * 0.6f) + ((100f - lastStrain) * 0.4f);
            result[current] = Math.Clamp(proxy, 0f, 100f);

            current = current.AddDays(1);
        }

        return result;
    }

    /// <summary>Average of the finite readings per UTC calendar day.</summary>
    private static Dictionary<DateOnly, float> DailyAverages(IReadOnlyList<BiometricEvent> events) =>
        events
            .Where(e => float.IsFinite((float)e.Value))
            .GroupBy(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime))
            .ToDictionary(g => g.Key, g => (float)g.Average(e => e.Value));

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A context for one call. ML.NET's internal log goes to our logger.</summary>
    private MLContext CreateContext()
    {
        var mlContext = new MLContext(seed: 42);
        mlContext.Log += OnMlContextLog;
        return mlContext;
    }

    private void OnMlContextLog(object? sender, LoggingEventArgs e)
    {
        // Route ML.NET internal logs to our structured logger at Debug level only.
        if (e.Kind >= Microsoft.ML.Runtime.ChannelMessageKind.Warning)
            _logger.LogDebug("[ML.NET] {Message}", e.Message);
    }
}
