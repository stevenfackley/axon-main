using System.ComponentModel;
using System.Runtime.CompilerServices;
using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.UI.ViewModels;

/// <summary>
/// ViewModel for the primary "Unified Vitals" dashboard.
///
/// Holds the latest biometric readings for all connected sources and exposes
/// the render-ready data for the SkiaSharp telemetry canvas.
///
/// Threading model:
///   • All writes to observable properties MUST occur on the UI thread.
///     The ingestion background service calls <c>Dispatcher.UIThread.Post</c>
///     before updating any property here.
///   • Heavy queries (QueryRangeAsync, GetAggregatesAsync) run on a background
///     thread via <c>Task.Run</c> and post results when complete.
///
/// LTTB contract:
///   • <see cref="ChartPoints"/> is ALWAYS a downsampled series.
///     The raw series NEVER reaches this ViewModel for spans > 24 hours.
/// </summary>
public sealed class DashboardViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Latest vitals (status-bar cards) ──────────────────────────────────────

    private double _heartRate;
    public double HeartRate
    {
        get => _heartRate;
        set => SetField(ref _heartRate, value);
    }

    private double _hrv;
    public double HeartRateVariability
    {
        get => _hrv;
        set => SetField(ref _hrv, value);
    }

    private double _spo2;
    public double SpO2
    {
        get => _spo2;
        set => SetField(ref _spo2, value);
    }

    private double _recoveryScore;
    public double RecoveryScore
    {
        get => _recoveryScore;
        set => SetField(ref _recoveryScore, value);
    }

    private double _readinessScore;
    public double ReadinessScore
    {
        get => _readinessScore;
        set => SetField(ref _readinessScore, value);
    }

    // ── Chart data (LTTB-downsampled, GPU-ready) ──────────────────────────────

    private IReadOnlyList<ChartPoint> _chartPoints = Array.Empty<ChartPoint>();
    /// <summary>
    /// Downsampled time-series for the active BiometricType in the viewport.
    /// Set by the LttbDownsamplingService after a range query completes.
    /// Max recommended count: 2048 points per render frame at 120fps.
    /// </summary>
    public IReadOnlyList<ChartPoint> ChartPoints
    {
        get => _chartPoints;
        set => SetField(ref _chartPoints, value);
    }

    private BiometricType _activeChartType = BiometricType.HeartRate;
    /// <summary>The metric currently displayed on the main telemetry canvas.</summary>
    public BiometricType ActiveChartType
    {
        get => _activeChartType;
        set => SetField(ref _activeChartType, value);
    }

    private DateTimeOffset _viewportStart = DateTimeOffset.UtcNow.AddHours(-24);
    public DateTimeOffset ViewportStart
    {
        get => _viewportStart;
        set => SetField(ref _viewportStart, value);
    }

    private DateTimeOffset _viewportEnd = DateTimeOffset.UtcNow;
    public DateTimeOffset ViewportEnd
    {
        get => _viewportEnd;
        set => SetField(ref _viewportEnd, value);
    }

    // ── ML Inference results ──────────────────────────────────────────────────

    private IReadOnlyList<AnomalyResult> _anomalyMarkers = Array.Empty<AnomalyResult>();
    /// <summary>
    /// IID Spike anomaly results from the last inference pass, bound to
    /// <see cref="Axon.UI.Rendering.SkiaTelemetryChart.AnomalyMarkers"/>.
    /// Updated on the UI thread after <see cref="IInferenceService.DetectAnomaliesAsync"/> completes.
    /// </summary>
    public IReadOnlyList<AnomalyResult> AnomalyMarkers
    {
        get => _anomalyMarkers;
        set => SetField(ref _anomalyMarkers, value);
    }

    private IReadOnlyList<ForecastPoint> _recoveryForecast = Array.Empty<ForecastPoint>();
    /// <summary>
    /// SSA-based 7-day recovery forecast from the last inference pass.
    /// Bound to the forecast sparkline overlay on the dashboard.
    /// </summary>
    public IReadOnlyList<ForecastPoint> RecoveryForecast
    {
        get => _recoveryForecast;
        set => SetField(ref _recoveryForecast, value);
    }

    private int _anomalyCount;
    /// <summary>
    /// Count of flagged anomalies in the current chart viewport.
    /// Displayed on the alert badge in the status bar.
    /// </summary>
    public int AnomalyCount
    {
        get => _anomalyCount;
        set
        {
            if (SetField(ref _anomalyCount, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAnomalies)));
        }
    }

    /// <summary>
    /// True when at least one anomaly has been flagged in the current viewport.
    /// Drives the visibility of the anomaly alert badge via BoolConverters.IsTrue.
    /// </summary>
    public bool HasAnomalies => _anomalyCount > 0;

    // ── Loading state ─────────────────────────────────────────────────────────

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>
/// Minimal struct for a single downsampled data point on the SkiaSharp canvas.
/// Kept as a <c>readonly struct</c> to enable stack allocation in render loops.
/// </summary>
public readonly record struct ChartPoint(DateTimeOffset Timestamp, double Value);
