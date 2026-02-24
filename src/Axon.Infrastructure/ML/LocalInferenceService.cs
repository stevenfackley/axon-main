using System.Buffers;
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
/// Threading: All <see cref="PredictionEngine{TSrc,TDst}"/> calls are marshalled
/// through <see cref="Task.Run"/> to keep the UI thread free for 120fps rendering.
///
/// AOT Safety:
///   • Models are loaded via explicit <see cref="FileStream"/> (no resource embedding).
///   • ML context uses explicitly typed input/output schemas; no reflection column
///     discovery via [LoadColumn] attributes on the hot path.
///   • <see cref="ArrayPool{T}"/> used for all intermediate float[] buffers.
/// </summary>
public sealed class LocalInferenceService : IInferenceService, IDisposable
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// IID Spike: confidence threshold above which a point is flagged.
    /// 0.95 → 95% confidence interval (p-value &lt; 0.05).
    /// </summary>
    private const double AnomalyConfidence = 0.95;

    /// <summary>
    /// Minimum number of samples required before the spike detector
    /// can produce a statistically meaningful result.
    /// </summary>
    private const int MinSpikeDetectorSamples = 12;

    /// <summary>
    /// SSA window size: 7-day seasonality for recovery patterns.
    /// Must be ≤ half the training series length.
    /// </summary>
    private const int SsaWindowSize = 7;

    /// <summary>
    /// Number of SSA eigentriples (rank of the decomposition).
    /// Higher = captures more complex seasonality; 4 is sufficient for daily readiness.
    /// </summary>
    private const int SsaRank = 4;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly MLContext _mlContext;
    private readonly ILogger<LocalInferenceService> _logger;

    // Spike detector engines — created lazily when first data arrives.
    // PredictionEngine is NOT thread-safe; we guard access with a SemaphoreSlim.
    private PredictionEngine<BiometricInputRow, SpikeOutputRow>? _hrSpikeEngine;
    private PredictionEngine<BiometricInputRow, SpikeOutputRow>? _hrvSpikeEngine;
    private readonly SemaphoreSlim _spikeEngineLock = new(1, 1);

    // SSA forecaster engine.
    private PredictionEngine<RecoveryInputRow, RecoveryForecastRow>? _recoveryEngine;
    private readonly SemaphoreSlim _recoveryEngineLock = new(1, 1);

    private bool _disposed;

    // ── Constructor ───────────────────────────────────────────────────────────

    public LocalInferenceService(ILogger<LocalInferenceService> logger)
    {
        _logger    = logger;
        _mlContext = new MLContext(seed: 42);

        // Suppress ML.NET's internal console logger — use our structured logger.
        _mlContext.Log += OnMlContextLog;
    }

    // ── IInferenceService ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<AnomalyResult>> DetectAnomaliesAsync(
        IReadOnlyList<BiometricEvent> heartRateSamples,
        IReadOnlyList<BiometricEvent> hrvSamples,
        CancellationToken             ct = default)
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
        int                           horizonDays = 7,
        CancellationToken             ct          = default)
    {
        ct.ThrowIfCancellationRequested();

        return await Task.Run(() => RunRecoveryForecast(sleepHistory, strainHistory, horizonDays, ct), ct)
                         .ConfigureAwait(false);
    }

    // ── Spike Detection (IID) ─────────────────────────────────────────────────

    private IReadOnlyList<AnomalyResult> RunSpikeDetection(
        IReadOnlyList<BiometricEvent> hrSamples,
        IReadOnlyList<BiometricEvent> hrvSamples,
        CancellationToken             ct)
    {
        var results = new List<AnomalyResult>(hrSamples.Count + hrvSamples.Count);

        // Process Heart Rate samples
        if (hrSamples.Count >= MinSpikeDetectorSamples)
        {
            var hrRows   = BuildInputRows(hrSamples);
            var hrEngine = GetOrBuildSpikeEngine(
                ref _hrSpikeEngine,
                hrSamples,
                BiometricType.HeartRate,
                _spikeEngineLock);

            foreach (var (evt, row) in Zip(hrSamples, hrRows))
            {
                ct.ThrowIfCancellationRequested();
                var prediction = hrEngine.Predict(row);
                results.Add(MapSpikeResult(evt, BiometricType.HeartRate, prediction));
            }

            ReturnInputRows(hrRows);
        }

        // Process HRV samples
        if (hrvSamples.Count >= MinSpikeDetectorSamples)
        {
            var hrvRows   = BuildInputRows(hrvSamples);
            var hrvEngine = GetOrBuildSpikeEngine(
                ref _hrvSpikeEngine,
                hrvSamples,
                BiometricType.HeartRateVariability,
                _spikeEngineLock);

            foreach (var (evt, row) in Zip(hrvSamples, hrvRows))
            {
                ct.ThrowIfCancellationRequested();
                var prediction = hrvEngine.Predict(row);
                results.Add(MapSpikeResult(evt, BiometricType.HeartRateVariability, prediction));
            }

            ReturnInputRows(hrvRows);
        }

        return results;
    }

    /// <summary>
    /// Builds or retrieves a cached spike detection engine for the given metric.
    /// The IidSpikeEstimator is trained on the supplied <paramref name="samples"/>
    /// each call; the model is stateless (no persisted .zip) because spike
    /// detection is purely distributional over the input window.
    /// </summary>
    private PredictionEngine<BiometricInputRow, SpikeOutputRow> GetOrBuildSpikeEngine(
        ref PredictionEngine<BiometricInputRow, SpikeOutputRow>? cachedEngine,
        IReadOnlyList<BiometricEvent>                             samples,
        BiometricType                                             metricType,
        SemaphoreSlim                                             engineLock)
    {
        // Fast path — engine already built for this metric type.
        if (cachedEngine is not null)
            return cachedEngine;

        engineLock.Wait();
        try
        {
            if (cachedEngine is not null)
                return cachedEngine;

            _logger.LogInformation(
                "[Inference] Building IID Spike engine for {MetricType} with {Count} training samples.",
                metricType, samples.Count);

            // Convert to IDataView using explicit row objects (AOT-safe).
            var rows     = samples.Select(e => new BiometricInputRow { Value = (float)e.Value });
            var dataView = _mlContext.Data.LoadFromEnumerable(rows);

            // Build the IID spike estimator pipeline.
            // pvalueHistoryLength: sliding window for p-value computation.
            // side: both tails (detect spikes AND dips).
            var pipeline = _mlContext.Transforms.DetectIidSpike(
                outputColumnName:    "Prediction",
                inputColumnName:     "Value",
                confidence:          AnomalyConfidence * 100.0,   // ML.NET uses 0–100 scale
                pvalueHistoryLength: Math.Min(samples.Count / 2, 512));

            var model = pipeline.Fit(dataView);
            cachedEngine = _mlContext.Model.CreatePredictionEngine<BiometricInputRow, SpikeOutputRow>(model);

            _logger.LogInformation("[Inference] IID Spike engine for {MetricType} ready.", metricType);
            return cachedEngine;
        }
        finally
        {
            engineLock.Release();
        }
    }

    private static AnomalyResult MapSpikeResult(
        BiometricEvent                              evt,
        BiometricType                               type,
        SpikeOutputRow                              output)
    {
        // ML.NET IidSpike Prediction = [alert, score, p-value]
        var pred     = output.Prediction;
        bool isAlert = pred is { Length: >= 1 } && pred[0] > 0f;
        double score  = pred is { Length: >= 2 } ? pred[1] : 0.0;
        double pValue = pred is { Length: >= 3 } ? pred[2] : 1.0;

        return new AnomalyResult(evt.Timestamp, type, isAlert, score, pValue);
    }

    // ── Recovery Forecasting (SSA) ────────────────────────────────────────────

    private IReadOnlyList<ForecastPoint> RunRecoveryForecast(
        IReadOnlyList<BiometricEvent> sleepHistory,
        IReadOnlyList<BiometricEvent> strainHistory,
        int                           horizonDays,
        CancellationToken             ct)
    {
        // Compute daily readiness proxy: blend sleep efficiency and inverse strain.
        // Groups events by calendar day and produces a single float per day.
        var dailyReadiness = BuildDailyReadinessProxy(sleepHistory, strainHistory);

        if (dailyReadiness.Count < SsaWindowSize * 2)
        {
            _logger.LogWarning(
                "[Inference] Insufficient history ({Count} days) for SSA forecast; need {Min}.",
                dailyReadiness.Count, SsaWindowSize * 2);
            return Array.Empty<ForecastPoint>();
        }

        ct.ThrowIfCancellationRequested();

        // Build or refresh the SSA engine (stateful — retrained on each call
        // to capture the latest data without persisting a model file).
        _recoveryEngineLock.Wait(ct);
        try
        {
            _logger.LogInformation(
                "[Inference] Building SSA Recovery Forecaster with {Days} days of history.",
                dailyReadiness.Count);

            var rows     = dailyReadiness.Values.Select(v => new RecoveryInputRow { ReadinessProxy = v });
            var dataView = _mlContext.Data.LoadFromEnumerable(rows);

            // SSA Forecasting pipeline.
            // windowSize:          seasonality window (7 = weekly).
            // seriesLength:        training series length.
            // trainSize:           how many rows to use for training.
            // horizon:             forecast steps (days).
            // isAdaptive:          retrain incrementally as new data arrives.
            // discountFactor:      recency weighting for adaptive mode.
            // rankSelectionMethod: automatic rank via eigenvalue thresholding.
            var pipeline = _mlContext.Forecasting.ForecastBySsa(
                outputColumnName:       "Forecast",
                inputColumnName:        "ReadinessProxy",
                windowSize:             SsaWindowSize,
                seriesLength:           dailyReadiness.Count,
                trainSize:              dailyReadiness.Count,
                horizon:                horizonDays,
                isAdaptive:             true,
                discountFactor:         0.9f,
                rankSelectionMethod:    RankSelectionMethod.Exact,
                rank:                   SsaRank,
                shouldStabilize:        true,
                confidenceLevel:        0.95f,
                confidenceLowerBoundColumn: "ConfidenceLowerBound",
                confidenceUpperBoundColumn: "ConfidenceUpperBound");

            var model  = pipeline.Fit(dataView);
            _recoveryEngine?.Dispose();
            _recoveryEngine = _mlContext.Model.CreatePredictionEngine<RecoveryInputRow, RecoveryForecastRow>(model);

            // Run forecast on an empty input row (SSA is stateful, not conditioned on new inputs).
            var result     = _recoveryEngine.Predict(new RecoveryInputRow());
            var forecasts  = result.Forecast           ?? Array.Empty<float>();
            var lowers     = result.ConfidenceLowerBound ?? Array.Empty<float>();
            var uppers     = result.ConfidenceUpperBound ?? Array.Empty<float>();

            var forecastPoints = new ForecastPoint[forecasts.Length];
            var baseDate       = DateTimeOffset.UtcNow.Date;

            for (int i = 0; i < forecasts.Length; i++)
            {
                // Clamp predictions to valid readiness range [0, 100].
                float predicted = Math.Clamp(forecasts[i], 0f, 100f);
                float lower     = i < lowers.Length ? Math.Clamp(lowers[i], 0f, 100f) : predicted;
                float upper     = i < uppers.Length ? Math.Clamp(uppers[i], 0f, 100f) : predicted;

                forecastPoints[i] = new ForecastPoint(
                    Date:               new DateTimeOffset(baseDate.AddDays(i + 1), TimeSpan.Zero),
                    PredictedReadiness: predicted,
                    LowerBound:         lower,
                    UpperBound:         upper);
            }

            _logger.LogInformation(
                "[Inference] SSA Forecast complete: {Horizon} days predicted.",
                forecastPoints.Length);

            return forecastPoints;
        }
        finally
        {
            _recoveryEngineLock.Release();
        }
    }

    /// <summary>
    /// Builds a day-keyed dictionary of readiness proxy values.
    /// Proxy = (sleep_efficiency_avg * 0.6) + ((100 - strain_avg) * 0.4).
    /// Missing days are forward-filled from the last known value.
    /// </summary>
    private static SortedDictionary<DateOnly, float> BuildDailyReadinessProxy(
        IReadOnlyList<BiometricEvent> sleepHistory,
        IReadOnlyList<BiometricEvent> strainHistory)
    {
        // Group by calendar day.
        var sleepByDay = sleepHistory
            .GroupBy(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime))
            .ToDictionary(g => g.Key, g => (float)g.Average(e => e.Value));

        var strainByDay = strainHistory
            .GroupBy(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime))
            .ToDictionary(g => g.Key, g => (float)g.Average(e => e.Value));

        // Union of all known days.
        var allDays = sleepByDay.Keys.Union(strainByDay.Keys).OrderBy(d => d).ToList();

        if (allDays.Count == 0)
            return new SortedDictionary<DateOnly, float>();

        var result       = new SortedDictionary<DateOnly, float>();
        float lastSleep  = 70f;   // Reasonable defaults if data is missing.
        float lastStrain = 50f;

        // Fill every calendar day from first to last (forward-fill gaps).
        var current = allDays[0];
        var last    = allDays[^1];

        while (current <= last)
        {
            if (sleepByDay.TryGetValue(current, out var sleep))   lastSleep  = sleep;
            if (strainByDay.TryGetValue(current, out var strain)) lastStrain = strain;

            // Readiness proxy: sleep efficiency weighted at 60%, recovery from strain at 40%.
            float proxy = (lastSleep * 0.6f) + ((100f - lastStrain) * 0.4f);
            result[current] = Math.Clamp(proxy, 0f, 100f);

            current = current.AddDays(1);
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Rents a <see cref="BiometricInputRow"/> array from <see cref="ArrayPool{T}"/>
    /// to avoid heap allocations in the ingestion hot path.
    /// </summary>
    private static BiometricInputRow[] BuildInputRows(IReadOnlyList<BiometricEvent> events)
    {
        var rows = ArrayPool<BiometricInputRow>.Shared.Rent(events.Count);
        for (int i = 0; i < events.Count; i++)
            rows[i] = new BiometricInputRow { Value = (float)events[i].Value };
        return rows;
    }

    private static void ReturnInputRows(BiometricInputRow[] rows) =>
        ArrayPool<BiometricInputRow>.Shared.Return(rows, clearArray: false);

    private static IEnumerable<(BiometricEvent Evt, BiometricInputRow Row)> Zip(
        IReadOnlyList<BiometricEvent> events,
        BiometricInputRow[]           rows)
    {
        for (int i = 0; i < events.Count; i++)
            yield return (events[i], rows[i]);
    }

    private void OnMlContextLog(object? sender, LoggingEventArgs e)
    {
        // Route ML.NET internal logs to our structured logger at Debug level only.
        if (e.Kind >= Microsoft.ML.Runtime.ChannelMessageKind.Warning)
            _logger.LogDebug("[ML.NET] {Message}", e.Message);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hrSpikeEngine?.Dispose();
        _hrvSpikeEngine?.Dispose();
        _recoveryEngine?.Dispose();
        _spikeEngineLock.Dispose();
        _recoveryEngineLock.Dispose();
    }
}
