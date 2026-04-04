using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.UI.Application;
using Axon.UI.Commands;

namespace Axon.UI.ViewModels;

/// <summary>
/// ViewModel for the primary "Unified Vitals" dashboard.
/// </summary>
public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private static readonly MetricOption[] MetricOptions =
    [
        new(BiometricType.HeartRate, "Heart Rate"),
        new(BiometricType.HeartRateVariability, "HRV"),
        new(BiometricType.SpO2, "SpO2"),
        new(BiometricType.RecoveryScore, "Recovery"),
        new(BiometricType.ReadinessScore, "Readiness"),
        new(BiometricType.StrainScore, "Strain")
    ];

    private static readonly ViewportPresetOption[] ViewportOptions =
    [
        new("1H", TimeSpan.FromHours(1)),
        new("6H", TimeSpan.FromHours(6)),
        new("24H", TimeSpan.FromHours(24)),
        new("7D", TimeSpan.FromDays(7)),
        new("30D", TimeSpan.FromDays(30)),
        new("90D", TimeSpan.FromDays(90)),
        new("1Y", TimeSpan.FromDays(365))
    ];

    private readonly IDashboardDataFacade _dashboardDataFacade;
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;
    private bool _isInitialized;
    private bool _suppressPresetRefresh;

    internal DashboardViewModel(IDashboardDataFacade dashboardDataFacade)
    {
        _dashboardDataFacade = dashboardDataFacade;

        AvailableChartTypes = MetricOptions;
        AvailableViewportPresets = ViewportOptions;

        _selectedMetric = MetricOptions[0];
        _selectedViewportPreset = ViewportOptions[2];
        ApplyViewportPreset(_selectedViewportPreset, DateTimeOffset.UtcNow);

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsLoading);
        PanLeftCommand = new AsyncCommand(PanLeftAsync, () => !IsLoading);
        PanRightCommand = new AsyncCommand(PanRightAsync, () => !IsLoading);
        ZoomInCommand = new AsyncCommand(ZoomInAsync, () => !IsLoading);
        ZoomOutCommand = new AsyncCommand(ZoomOutAsync, () => !IsLoading);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<MetricOption> AvailableChartTypes { get; }

    public IReadOnlyList<ViewportPresetOption> AvailableViewportPresets { get; }

    public ICommand RefreshCommand { get; }

    public ICommand PanLeftCommand { get; }

    public ICommand PanRightCommand { get; }

    public ICommand ZoomInCommand { get; }

    public ICommand ZoomOutCommand { get; }

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
    public IReadOnlyList<ChartPoint> ChartPoints
    {
        get => _chartPoints;
        set => SetField(ref _chartPoints, value);
    }

    private MetricOption _selectedMetric;
    public MetricOption SelectedMetric
    {
        get => _selectedMetric;
        set
        {
            if (!SetField(ref _selectedMetric, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveChartType)));
            if (_isInitialized) _ = RefreshAsync();
        }
    }

    public BiometricType ActiveChartType => SelectedMetric.Type;

    private ViewportPresetOption _selectedViewportPreset;
    public ViewportPresetOption SelectedViewportPreset
    {
        get => _selectedViewportPreset;
        set
        {
            if (!SetField(ref _selectedViewportPreset, value)) return;
            var anchor = _isInitialized ? ViewportEnd : DateTimeOffset.UtcNow;
            ApplyViewportPreset(value, anchor);
            if (_isInitialized && !_suppressPresetRefresh) _ = RefreshAsync();
        }
    }

    private DateTimeOffset _viewportStart;
    public DateTimeOffset ViewportStart
    {
        get => _viewportStart;
        set => SetField(ref _viewportStart, value);
    }

    private DateTimeOffset _viewportEnd;
    public DateTimeOffset ViewportEnd
    {
        get => _viewportEnd;
        set => SetField(ref _viewportEnd, value);
    }

    private double _chartMinValue = 40d;
    public double ChartMinValue
    {
        get => _chartMinValue;
        set => SetField(ref _chartMinValue, value);
    }

    private double _chartMaxValue = 200d;
    public double ChartMaxValue
    {
        get => _chartMaxValue;
        set => SetField(ref _chartMaxValue, value);
    }

    // ── ML Inference results ──────────────────────────────────────────────────

    private IReadOnlyList<AnomalyResult> _anomalyMarkers = Array.Empty<AnomalyResult>();
    public IReadOnlyList<AnomalyResult> AnomalyMarkers
    {
        get => _anomalyMarkers;
        set => SetField(ref _anomalyMarkers, value);
    }

    private IReadOnlyList<ForecastPoint> _recoveryForecast = Array.Empty<ForecastPoint>();
    public IReadOnlyList<ForecastPoint> RecoveryForecast
    {
        get => _recoveryForecast;
        set => SetField(ref _recoveryForecast, value);
    }

    private int _anomalyCount;
    public int AnomalyCount
    {
        get => _anomalyCount;
        set
        {
            if (SetField(ref _anomalyCount, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAnomalies)));
            }
        }
    }

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

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        int version = Interlocked.Increment(ref _loadVersion);
        var cts = ReplaceLoadCancellation();

        try
        {
            IsLoading = true;
            ErrorMessage = null;
            RaiseCommandStates();

            var snapshot = await _dashboardDataFacade.LoadAsync(
                ActiveChartType,
                ViewportStart,
                ViewportEnd,
                cts.Token);

            if (version != _loadVersion || cts.IsCancellationRequested)
            {
                return;
            }

            HeartRate = snapshot.HeartRate;
            HeartRateVariability = snapshot.HeartRateVariability;
            SpO2 = snapshot.SpO2;
            RecoveryScore = snapshot.RecoveryScore;
            ReadinessScore = snapshot.ReadinessScore;
            ChartPoints = snapshot.ChartSeries.Points;
            ChartMinValue = snapshot.ChartSeries.MinValue;
            ChartMaxValue = snapshot.ChartSeries.MaxValue;
            AnomalyMarkers = snapshot.Anomalies;
            AnomalyCount = snapshot.Anomalies.Count;
            RecoveryForecast = snapshot.RecoveryForecast;
        }
        catch (OperationCanceledException)
        {
            // A newer request superseded the current viewport load.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }

            RaiseCommandStates();
        }
    }

    private async Task PanLeftAsync()
    {
        ShiftViewport(-0.5d);
        await RefreshAsync();
    }

    private async Task PanRightAsync()
    {
        ShiftViewport(0.5d);
        await RefreshAsync();
    }

    private async Task ZoomInAsync()
    {
        if (!TryMovePreset(-1))
        {
            return;
        }

        await RefreshAsync();
    }

    private async Task ZoomOutAsync()
    {
        if (!TryMovePreset(1))
        {
            return;
        }

        await RefreshAsync();
    }

    private void ApplyViewportPreset(ViewportPresetOption preset, DateTimeOffset anchor)
    {
        ViewportEnd = anchor;
        ViewportStart = anchor - preset.Span;
    }

    private void ShiftViewport(double fraction)
    {
        var shift = TimeSpan.FromTicks((long)(SelectedViewportPreset.Span.Ticks * fraction));
        ViewportStart = ViewportStart.Add(shift);
        ViewportEnd = ViewportEnd.Add(shift);
    }

    private bool TryMovePreset(int offset)
    {
        int index = Array.IndexOf(ViewportOptions, SelectedViewportPreset);
        int nextIndex = Math.Clamp(index + offset, 0, ViewportOptions.Length - 1);
        if (nextIndex == index)
        {
            return false;
        }

        _suppressPresetRefresh = true;
        try
        {
            SelectedViewportPreset = ViewportOptions[nextIndex];
        }
        finally
        {
            _suppressPresetRefresh = false;
        }

        return true;
    }

    private CancellationTokenSource ReplaceLoadCancellation()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        return _loadCts;
    }

    private void RaiseCommandStates()
    {
        if (RefreshCommand is AsyncCommand refresh) refresh.RaiseCanExecuteChanged();
        if (PanLeftCommand is AsyncCommand panLeft) panLeft.RaiseCanExecuteChanged();
        if (PanRightCommand is AsyncCommand panRight) panRight.RaiseCanExecuteChanged();
        if (ZoomInCommand is AsyncCommand zoomIn) zoomIn.RaiseCanExecuteChanged();
        if (ZoomOutCommand is AsyncCommand zoomOut) zoomOut.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public readonly record struct ChartPoint(DateTimeOffset Timestamp, double Value);
