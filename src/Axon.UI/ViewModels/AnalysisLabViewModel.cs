using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Analytics;
using Axon.UI.Application;
using Axon.UI.Commands;

namespace Axon.UI.ViewModels;

public sealed class AnalysisLabViewModel : INotifyPropertyChanged
{
    private static readonly MetricOption[] MetricOptions =
    [
        new(BiometricType.HeartRate, "Heart Rate"),
        new(BiometricType.HeartRateVariability, "HRV"),
        new(BiometricType.SpO2, "SpO2"),
        new(BiometricType.SleepEfficiency, "Sleep Efficiency"),
        new(BiometricType.RecoveryScore, "Recovery"),
        new(BiometricType.ReadinessScore, "Readiness"),
        new(BiometricType.StrainScore, "Strain")
    ];

    private static readonly AnalysisTimeframeOption[] Timeframes =
    [
        new("7D", TimeSpan.FromDays(7)),
        new("30D", TimeSpan.FromDays(30)),
        new("90D", TimeSpan.FromDays(90)),
        new("180D", TimeSpan.FromDays(180))
    ];

    private readonly IAnalysisLabFacade _analysisLabFacade;
    private readonly ITagRepository _tagRepository;
    private readonly IBiometricRepository _biometricRepository;
    private readonly TagCorrelationAnalyzer _tagAnalyzer = new();
    private CancellationTokenSource? _loadCts;
    private bool _isInitialized;

    internal AnalysisLabViewModel(
        IAnalysisLabFacade analysisLabFacade,
        ITagRepository tagRepository,
        IBiometricRepository biometricRepository)
    {
        _analysisLabFacade = analysisLabFacade;
        _tagRepository = tagRepository;
        _biometricRepository = biometricRepository;
        AvailableMetrics = MetricOptions;
        AvailableTimeframes = Timeframes;

        _primaryMetric = MetricOptions[3];
        _secondaryMetric = MetricOptions[4];
        _selectedTimeframe = Timeframes[1];

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsLoading);
        AddTagTodayCommand = new AsyncCommand(AddTagTodayAsync);
        ComputeTagCorrelationsCommand = new AsyncCommand(ComputeTagCorrelationsAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<MetricOption> AvailableMetrics { get; }

    public IReadOnlyList<AnalysisTimeframeOption> AvailableTimeframes { get; }

    public ICommand RefreshCommand { get; }

    private MetricOption _primaryMetric;
    public MetricOption PrimaryMetric
    {
        get => _primaryMetric;
        set
        {
            if (!SetField(ref _primaryMetric, value)) return;
            if (_isInitialized) _ = RefreshAsync();
        }
    }

    private MetricOption _secondaryMetric;
    public MetricOption SecondaryMetric
    {
        get => _secondaryMetric;
        set
        {
            if (!SetField(ref _secondaryMetric, value)) return;
            if (_isInitialized) _ = RefreshAsync();
        }
    }

    private AnalysisTimeframeOption _selectedTimeframe;
    public AnalysisTimeframeOption SelectedTimeframe
    {
        get => _selectedTimeframe;
        set
        {
            if (!SetField(ref _selectedTimeframe, value)) return;
            if (_isInitialized) _ = RefreshAsync();
        }
    }

    private IReadOnlyList<AnalysisScatterPointViewModel> _scatterPoints = Array.Empty<AnalysisScatterPointViewModel>();
    public IReadOnlyList<AnalysisScatterPointViewModel> ScatterPoints
    {
        get => _scatterPoints;
        set => SetField(ref _scatterPoints, value);
    }

    private double _correlationCoefficient;
    public double CorrelationCoefficient
    {
        get => _correlationCoefficient;
        set => SetField(ref _correlationCoefficient, value);
    }

    private string _correlationLabel = "Not loaded";
    public string CorrelationLabel
    {
        get => _correlationLabel;
        set => SetField(ref _correlationLabel, value);
    }

    private string _bucketLabel = "Not loaded";
    public string BucketLabel
    {
        get => _bucketLabel;
        set => SetField(ref _bucketLabel, value);
    }

    private string _insightHeadline = "Load a metric pair to explore how signals move together.";
    public string InsightHeadline
    {
        get => _insightHeadline;
        set => SetField(ref _insightHeadline, value);
    }

    private double _primaryAverage;
    public double PrimaryAverage
    {
        get => _primaryAverage;
        set => SetField(ref _primaryAverage, value);
    }

    private double _secondaryAverage;
    public double SecondaryAverage
    {
        get => _secondaryAverage;
        set => SetField(ref _secondaryAverage, value);
    }

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

    // ── Custom tags + correlation (rec #5) ────────────────────────────────────

    public ICommand AddTagTodayCommand { get; }

    public ICommand ComputeTagCorrelationsCommand { get; }

    private string _newTagName = "";
    /// <summary>The tag name the user is about to record (e.g. "caffeine", "alcohol").</summary>
    public string NewTagName
    {
        get => _newTagName;
        set => SetField(ref _newTagName, value);
    }

    private string _tagStatus = "Tag days (caffeine, alcohol, travel…), then correlate against the primary metric.";
    public string TagStatus
    {
        get => _tagStatus;
        set => SetField(ref _tagStatus, value);
    }

    private IReadOnlyList<string> _tagCorrelations = Array.Empty<string>();
    /// <summary>Ranked tag-correlation result lines for display.</summary>
    public IReadOnlyList<string> TagCorrelations
    {
        get => _tagCorrelations;
        set => SetField(ref _tagCorrelations, value);
    }

    private async Task AddTagTodayAsync()
    {
        var name = NewTagName.Trim();
        if (name.Length == 0) return;

        try
        {
            var tags = await _tagRepository.GetTagsAsync();
            var tag = tags.FirstOrDefault(
                t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
            {
                tag = new Tag(Guid.NewGuid(), name, "custom");
                await _tagRepository.AddTagAsync(tag);
            }

            await _tagRepository.AddAnnotationAsync(
                new TagAnnotation(tag.Id, DateTimeOffset.UtcNow, Value: null, Note: null));

            TagStatus = $"Tagged today as '{name}'. Tag more days, then Compute correlations.";
            NewTagName = "";
        }
        catch (Exception ex)
        {
            TagStatus = $"Could not save tag: {ex.Message}";
        }
    }

    private async Task ComputeTagCorrelationsAsync()
    {
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = to - SelectedTimeframe.Span;

            var tags = await _tagRepository.GetTagsAsync();
            var annotations = await _tagRepository.GetAnnotationsAsync(from, to);
            var events = await _biometricRepository.QueryRangeAsync(PrimaryMetric.Type, from, to);

            var daily = events
                .GroupBy(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime))
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Average(e => e.Value)))
                .ToList();

            var results = _tagAnalyzer.Analyze(annotations, daily, tags);

            TagCorrelations = results.Count == 0
                ? ["No correlations yet — tag several days and make sure the primary metric has data in this window."]
                : results.Select(r =>
                    $"{r.TagName}: {r.Strength} — {PrimaryMetric.Label} is {r.EffectSize:+0.0;-0.0} on tagged days " +
                    $"(r={r.Coefficient:0.00}, n={r.SampleSize})").ToArray();

            TagStatus = $"Analyzed {tags.Count} tag(s) against {PrimaryMetric.Label} over {SelectedTimeframe.Label}.";
        }
        catch (Exception ex)
        {
            TagStatus = $"Correlation failed: {ex.Message}";
        }
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        ReplaceCancellation();
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            if (RefreshCommand is AsyncCommand refresh) refresh.RaiseCanExecuteChanged();

            var to = DateTimeOffset.UtcNow;
            var from = to - SelectedTimeframe.Span;
            var snapshot = await _analysisLabFacade.AnalyzeAsync(
                PrimaryMetric.Type,
                SecondaryMetric.Type,
                from,
                to,
                _loadCts!.Token);

            ScatterPoints = snapshot.Points
                .Select(p => new AnalysisScatterPointViewModel(p.Timestamp, p.PrimaryValue, p.SecondaryValue))
                .ToArray();
            CorrelationCoefficient = snapshot.CorrelationCoefficient;
            CorrelationLabel = snapshot.CorrelationLabel;
            BucketLabel = snapshot.BucketLabel;
            InsightHeadline = snapshot.InsightHeadline;
            PrimaryAverage = snapshot.PrimaryAverage;
            SecondaryAverage = snapshot.SecondaryAverage;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            if (RefreshCommand is AsyncCommand refresh) refresh.RaiseCanExecuteChanged();
        }
    }

    private void ReplaceCancellation()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
