using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.ML;
using Axon.UI.Application;
using Axon.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Axon.Tests;

/// <summary>
/// The dashboard load around the two models: a model failure must not take the vitals and
/// chart down with it, and the insight cards must be built from every scored point.
/// </summary>
public sealed class DashboardDataFacadeTests
{
    // Relative to now, because the dashboard view model anchors its viewport on the clock.
    private static readonly DateTimeOffset End = DateTimeOffset.UtcNow.AddMinutes(-5);

    [Fact]
    public async Task LoadAsync_BothModelsThrow_VitalsAndChartStillLoad()
    {
        var facade = NewFacade(InMemoryRepository.WithHistory(), new ThrowingInference());

        var snapshot = await facade.LoadAsync(BiometricType.HeartRate, End.AddDays(-1), End);

        Assert.NotEmpty(snapshot.ChartSeries.Points);
        Assert.Equal(64, snapshot.HeartRate);
        Assert.Equal("anomaly model broke", snapshot.AnomalyModelError);
        Assert.Equal("forecast model broke", snapshot.ForecastModelError);
        Assert.Empty(snapshot.AnomalyResults);
        Assert.Empty(snapshot.Anomalies);
        Assert.Empty(snapshot.RecoveryForecast);
    }

    [Fact]
    public async Task LoadAsync_OneModelThrows_TheOtherStillAnswers()
    {
        var inference = new ThrowingInference { ForecastAnswer = [new ForecastPoint(End.AddDays(1), 70, 60, 80)] };
        var facade = NewFacade(InMemoryRepository.WithHistory(), inference);

        var snapshot = await facade.LoadAsync(BiometricType.HeartRate, End.AddDays(-1), End);

        Assert.NotNull(snapshot.AnomalyModelError);
        Assert.Null(snapshot.ForecastModelError);
        Assert.Single(snapshot.RecoveryForecast);
    }

    [Fact]
    public async Task LoadAsync_RealModelsOnRealisticHistory_RunWithoutError()
    {
        var facade = NewFacade(
            InMemoryRepository.WithHistory(),
            new LocalInferenceService(NullLogger<LocalInferenceService>.Instance));

        var snapshot = await facade.LoadAsync(BiometricType.HeartRate, End.AddDays(-14), End);

        Assert.Null(snapshot.AnomalyModelError);
        Assert.Null(snapshot.ForecastModelError);
        // The detector is fitted on the 28 days up to the newest reading; its baseline is the
        // first half, so all 14 viewport days of hourly HR and HRV (inclusive) are scored.
        Assert.Equal(2 * ((14 * 24) + 1), snapshot.AnomalyResults.Count);
        Assert.Equal(snapshot.AnomalyResults.Where(a => a.IsAnomaly), snapshot.Anomalies);
        Assert.Equal(7, snapshot.RecoveryForecast.Count);
        Assert.Equal(30, snapshot.ForecastHistoryDays);
    }

    [Fact]
    public async Task Dashboard_ModelFailure_ShowsTheCardAsUnavailable_NotAPageError()
    {
        var vm = new DashboardViewModel(NewFacade(InMemoryRepository.WithHistory(), new ThrowingInference()));

        await vm.InitializeAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.NotEmpty(vm.ChartPoints);
        Assert.Equal("Anomaly Detection — Unavailable", vm.AnomalyInsightTitle);
        Assert.Contains("anomaly model broke", vm.AnomalyInsightDetail);
        Assert.Equal("Recovery Outlook — Unavailable", vm.RecoveryInsightTitle);
    }

    [Fact]
    public async Task Dashboard_InsightCountsEveryScoredPoint_NotOnlyTheFlaggedOnes()
    {
        var results = Enumerable.Range(0, 40)
            .Select(i => new AnomalyResult(End.AddHours(-i), BiometricType.HeartRate, IsAnomaly: i == 3, 9, i == 3 ? 0.001 : 0.5))
            .ToArray();
        var forecast = Enumerable.Range(1, 7).Select(d => new ForecastPoint(End.AddDays(d), 75, 70, 80)).ToArray();
        var inference = new ThrowingInference { AnomalyAnswer = results, ForecastAnswer = forecast };
        var vm = new DashboardViewModel(NewFacade(InMemoryRepository.WithHistory(), inference));

        await vm.InitializeAsync();

        // 24 of the 40 points fall in the default 24-hour viewport; the card names the metric.
        Assert.Single(vm.AnomalyMarkers);
        Assert.StartsWith("Heart Rate: ", vm.AnomalyInsightDetail);
        Assert.Contains("1 out of 24", vm.AnomalyInsightDetail);
        // Confidence comes from the 30 days of sleep/strain history, not the anomaly count.
        Assert.Equal("Moderate (n=30)", vm.RecoveryInsightConfidence);
    }

    [Fact]
    public async Task LoadAsync_ScoresTheSameWindowWhateverTheViewport_AndShowsOnlyTheViewportsFlags()
    {
        var inference = new RecordingInference();
        var facade = NewFacade(InMemoryRepository.WithHistory(), inference);

        var today = await facade.LoadAsync(BiometricType.HeartRate, End.AddDays(-1), End);
        var earlier = await facade.LoadAsync(BiometricType.HeartRate, End.AddDays(-3), End.AddDays(-2));

        // Both loads fitted the detector on the same 28 days, ending at the newest reading.
        Assert.Equal(inference.Windows[0], inference.Windows[1]);
        Assert.Equal((End.AddDays(-28), End), inference.Windows[0]);
        Assert.All(today.Anomalies, a => Assert.InRange(a.Timestamp, End.AddDays(-1), End));
        Assert.All(earlier.Anomalies, a => Assert.InRange(a.Timestamp, End.AddDays(-3), End.AddDays(-2)));
        Assert.NotEmpty(today.Anomalies);
        Assert.NotEmpty(earlier.Anomalies);
    }

    [Fact]
    public async Task Dashboard_TheCardDescribesOneMetric_NeverPoolingHeartRateAndHrv()
    {
        // HR: nothing flagged in 20 points; HRV: 5 of 20 flagged.
        var results = Enumerable.Range(0, 20)
            .Select(i => new AnomalyResult(End.AddHours(-i), BiometricType.HeartRate, false, 1, 0.5))
            .Concat(Enumerable.Range(0, 20)
                .Select(i => new AnomalyResult(End.AddHours(-i), BiometricType.HeartRateVariability, i < 5, 9, i < 5 ? 0.001 : 0.5)))
            .ToArray();
        var inference = new ThrowingInference { AnomalyAnswer = results, ForecastAnswer = [] };
        var vm = new DashboardViewModel(NewFacade(InMemoryRepository.WithHistory(), inference));

        await vm.InitializeAsync();

        Assert.StartsWith("HRV: 5 out of 20 readings (25%)", vm.AnomalyInsightDetail);
    }

    /// <summary>Notes the HR window each call was fitted on and flags every sixth hour.</summary>
    private sealed class RecordingInference : IInferenceService
    {
        public List<(DateTimeOffset First, DateTimeOffset Last)> Windows { get; } = [];

        public ValueTask<IReadOnlyList<AnomalyResult>> DetectAnomaliesAsync(
            IReadOnlyList<BiometricEvent> heartRateSamples,
            IReadOnlyList<BiometricEvent> hrvSamples,
            CancellationToken ct = default)
        {
            Windows.Add((heartRateSamples[0].Timestamp, heartRateSamples[^1].Timestamp));
            IReadOnlyList<AnomalyResult> results = heartRateSamples
                .Select((e, i) => new AnomalyResult(e.Timestamp, BiometricType.HeartRate, i % 6 == 0, 1, 0.5))
                .ToArray();
            return ValueTask.FromResult(results);
        }

        public ValueTask<IReadOnlyList<ForecastPoint>> ForecastRecoveryAsync(
            IReadOnlyList<BiometricEvent> sleepHistory,
            IReadOnlyList<BiometricEvent> strainHistory,
            int horizonDays = 7,
            CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<ForecastPoint>>([]);
    }

    private static DashboardDataFacade NewFacade(IBiometricRepository repository, IInferenceService inference) =>
        new(repository, inference, new RawChartSeriesStrategy(TimeSpan.FromDays(400), threshold: 100_000));

    /// <summary>Throws from each model unless an answer is set for it.</summary>
    private sealed class ThrowingInference : IInferenceService
    {
        public IReadOnlyList<AnomalyResult>? AnomalyAnswer { get; init; }
        public IReadOnlyList<ForecastPoint>? ForecastAnswer { get; init; }

        public ValueTask<IReadOnlyList<AnomalyResult>> DetectAnomaliesAsync(
            IReadOnlyList<BiometricEvent> heartRateSamples,
            IReadOnlyList<BiometricEvent> hrvSamples,
            CancellationToken ct = default) =>
            AnomalyAnswer is null
                ? throw new InvalidOperationException("anomaly model broke")
                : ValueTask.FromResult(AnomalyAnswer);

        public ValueTask<IReadOnlyList<ForecastPoint>> ForecastRecoveryAsync(
            IReadOnlyList<BiometricEvent> sleepHistory,
            IReadOnlyList<BiometricEvent> strainHistory,
            int horizonDays = 7,
            CancellationToken ct = default) =>
            ForecastAnswer is null
                ? throw new InvalidOperationException("forecast model broke")
                : ValueTask.FromResult(ForecastAnswer);
    }

    private sealed class InMemoryRepository(List<BiometricEvent> events) : IBiometricRepository
    {
        /// <summary>30 days: hourly HR and HRV, one sleep and one strain reading per day.</summary>
        public static InMemoryRepository WithHistory()
        {
            var events = new List<BiometricEvent>();
            for (int h = 0; h < 30 * 24; h++)
            {
                var ts = End.AddHours(-h);
                events.Add(Event(BiometricType.HeartRate, ts, h == 0 ? 64 : 60 + (h % 5)));
                events.Add(Event(BiometricType.HeartRateVariability, ts, 50 + (h % 7)));
            }
            for (int d = 0; d < 30; d++)
            {
                var ts = End.AddDays(-d).AddHours(-4);
                events.Add(Event(BiometricType.SleepEfficiency, ts, 78 + (d % 4)));
                events.Add(Event(BiometricType.StrainScore, ts, 35 + (d % 6)));
            }
            return new InMemoryRepository(events);
        }

        public ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<BiometricEvent>>(events
                .Where(e => e.Type == type && e.Timestamp >= from && e.Timestamp <= to)
                .OrderBy(e => e.Timestamp)
                .ToList());

        public ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
            CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyDictionary<BiometricType, BiometricEvent>>(events
                .GroupBy(e => e.Type)
                .ToDictionary(g => g.Key, g => g.MaxBy(e => e.Timestamp)!));

        public IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, int bucketSizeSeconds,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask IngestBatchAsync(IReadOnlyList<BiometricEvent> batch, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<BiometricEvent?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask AddAsync(BiometricEvent entity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask AddRangeAsync(IReadOnlyList<BiometricEvent> entities, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask UpdateAsync(BiometricEvent entity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        private static BiometricEvent Event(BiometricType type, DateTimeOffset ts, double value) =>
            new(Guid.NewGuid(), ts, type, value, "unit", new SourceMetadata("dev-1", "Test", null, 0.9f, ts));
    }
}
