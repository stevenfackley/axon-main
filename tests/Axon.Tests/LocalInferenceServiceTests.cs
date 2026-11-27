using Axon.Core.Domain;
using Axon.Infrastructure.ML;
using Microsoft.Extensions.Logging.Abstractions;

namespace Axon.Tests;

/// <summary>
/// Runs both ML.NET models in <see cref="LocalInferenceService"/> on synthetic series long
/// enough to pass their minimum-sample thresholds (12 HR/HRV samples, 15 days of history),
/// and checks the answers are sane, not only that nothing throws.
/// </summary>
public sealed class LocalInferenceServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly LocalInferenceService _service = new(NullLogger<LocalInferenceService>.Instance);

    // ── Spike detection ───────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAnomalies_RealisticWindow_ReturnsOneResultPerFlaggableSample()
    {
        var hr = Series(BiometricType.HeartRate, Wobble(60, 48), TimeSpan.FromMinutes(30));
        var hrv = Series(BiometricType.HeartRateVariability, Wobble(55, 48), TimeSpan.FromMinutes(30));

        var results = await _service.DetectAnomaliesAsync(hr, hrv);

        // 48 samples: a 24-point p-value window, so the first 23 are baseline, not results.
        Assert.Equal(hr.Skip(23).Select(e => e.Timestamp),
            results.Where(r => r.BiometricType == BiometricType.HeartRate).Select(r => r.Timestamp));
        Assert.Equal(25, results.Count(r => r.BiometricType == BiometricType.HeartRateVariability));
        Assert.All(results, r => Assert.InRange(r.PValue, 0d, 1d));
    }

    [Fact]
    public async Task DetectAnomalies_ClearSpikeInFlatSeries_FlagsThatSampleOnly()
    {
        var values = Wobble(60, 40);
        values[30] = 160;
        var hr = Series(BiometricType.HeartRate, values, TimeSpan.FromMinutes(10));

        var results = await _service.DetectAnomaliesAsync(hr, []);

        var flagged = results.Where(r => r.IsAnomaly).ToList();
        Assert.Single(flagged);
        Assert.Equal(hr[30].Timestamp, flagged[0].Timestamp);
        Assert.Equal(BiometricType.HeartRate, flagged[0].BiometricType);
    }

    [Fact]
    public async Task DetectAnomalies_TheBaselineHalf_IsNotReturned()
    {
        // The p-value window is half the series; the detector raises no alert until it has
        // filled it, so those points are its baseline and are not reported as "normal".
        var values = Wobble(60, 40);
        values[10] = 160;
        var hr = Series(BiometricType.HeartRate, values, TimeSpan.FromMinutes(10));

        var results = await _service.DetectAnomaliesAsync(hr, []);

        Assert.Equal(21, results.Count);
        Assert.Equal(hr[19].Timestamp, results[0].Timestamp);
        Assert.DoesNotContain(results, r => r.IsAnomaly);
    }

    [Fact]
    public async Task DetectAnomalies_FlatSeries_FlagsNothing()
    {
        var constant = Series(BiometricType.HeartRate, Enumerable.Repeat(62d, 40).ToArray(), TimeSpan.FromMinutes(10));
        var wobble = Series(BiometricType.HeartRateVariability, Wobble(50, 40), TimeSpan.FromMinutes(10));

        var results = await _service.DetectAnomaliesAsync(constant, wobble);

        Assert.Equal(2 * 21, results.Count);
        Assert.DoesNotContain(results, r => r.IsAnomaly);
    }

    [Fact]
    public async Task DetectAnomalies_UnderTwelveSamples_ReturnsNothingForThatMetric()
    {
        var hr = Series(BiometricType.HeartRate, Wobble(60, 11), TimeSpan.FromMinutes(10));
        var hrv = Series(BiometricType.HeartRateVariability, Wobble(50, 12), TimeSpan.FromMinutes(10));

        var results = await _service.DetectAnomaliesAsync(hr, hrv);

        Assert.DoesNotContain(results, r => r.BiometricType == BiometricType.HeartRate);
        Assert.Equal(7, results.Count(r => r.BiometricType == BiometricType.HeartRateVariability));
    }

    [Fact]
    public async Task DetectAnomalies_NaNSamples_AreSkippedNotFatal()
    {
        var values = Wobble(60, 40);
        values[5] = double.NaN;
        values[17] = double.PositiveInfinity;
        values[30] = 160;
        var hr = Series(BiometricType.HeartRate, values, TimeSpan.FromMinutes(10));

        var results = await _service.DetectAnomaliesAsync(hr, []);

        // 38 finite samples: a 19-point p-value window, so 20 results.
        Assert.Equal(20, results.Count);
        Assert.DoesNotContain(results, r => double.IsNaN(r.Score) || double.IsInfinity(r.Score));
        Assert.Equal(hr[30].Timestamp, Assert.Single(results, r => r.IsAnomaly).Timestamp);
    }

    [Fact]
    public async Task DetectAnomalies_EachCallIsFittedOnItsOwnWindow()
    {
        // Two different windows run at the same time (the dashboard and the post-ingest
        // pass both call the detector). Each answer must come from its own series.
        var first = Wobble(60, 40);
        first[24] = 170;
        var second = Wobble(60, 40);
        second[33] = 170;
        var hrA = Series(BiometricType.HeartRate, first, TimeSpan.FromMinutes(10));
        var hrB = Series(BiometricType.HeartRate, second, TimeSpan.FromMinutes(10), Start.AddDays(3));

        var runs = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => _service.DetectAnomaliesAsync(i % 2 == 0 ? hrA : hrB, []).AsTask()));

        for (int i = 0; i < runs.Length; i++)
        {
            var expected = i % 2 == 0 ? hrA[24].Timestamp : hrB[33].Timestamp;
            Assert.Equal(expected, Assert.Single(runs[i], r => r.IsAnomaly).Timestamp);
        }
    }

    // ── Recovery forecast ─────────────────────────────────────────────────────

    [Fact]
    public async Task ForecastRecovery_RealisticHistory_ReturnsTheRequestedHorizon()
    {
        var sleep = Daily(BiometricType.SleepEfficiency, Enumerable.Range(0, 30).Select(d => 78d + (d % 7) - 3).ToArray());
        var strain = Daily(BiometricType.StrainScore, Enumerable.Range(0, 30).Select(d => 40d + (d % 5) * 2).ToArray());

        var forecast = await _service.ForecastRecoveryAsync(sleep, strain, horizonDays: 7);

        Assert.Equal(7, forecast.Count);
        Assert.All(forecast, p =>
        {
            Assert.InRange(p.PredictedReadiness, 0f, 100f);
            Assert.True(p.LowerBound <= p.PredictedReadiness && p.PredictedReadiness <= p.UpperBound);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task ForecastRecovery_SteadySeries_StaysNearThatLevel(int horizon)
    {
        // Proxy = sleep * 0.6 + (100 - strain) * 0.4 = 80 * 0.6 + 70 * 0.4 = 76.
        var sleep = Daily(BiometricType.SleepEfficiency, Wobble(80, 28));
        var strain = Daily(BiometricType.StrainScore, Enumerable.Repeat(30d, 28).ToArray());

        var before = DateTimeOffset.UtcNow.Date;
        var forecast = await _service.ForecastRecoveryAsync(sleep, strain, horizon);
        var after = DateTimeOffset.UtcNow.Date;

        Assert.Equal(horizon, forecast.Count);
        Assert.All(forecast, p => Assert.InRange(p.PredictedReadiness, 73f, 79f));
        // One point per day, starting tomorrow.
        var firstDay = forecast[0].Date.UtcDateTime;
        Assert.True(firstDay == before.AddDays(1) || firstDay == after.AddDays(1));
        Assert.Equal(
            Enumerable.Range(0, horizon).Select(d => firstDay.AddDays(d)),
            forecast.Select(p => p.Date.UtcDateTime));
    }

    [Fact]
    public async Task ForecastRecovery_ConstantSeries_ForecastsThatConstant()
    {
        var sleep = Daily(BiometricType.SleepEfficiency, Enumerable.Repeat(80d, 20).ToArray());
        var strain = Daily(BiometricType.StrainScore, Enumerable.Repeat(30d, 20).ToArray());

        var forecast = await _service.ForecastRecoveryAsync(sleep, strain, horizonDays: 7);

        Assert.Equal(7, forecast.Count);
        Assert.All(forecast, p => Assert.InRange(p.PredictedReadiness, 75f, 77f));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    public async Task ForecastRecovery_FourteenDaysOrFewer_ReturnsNothing(int days)
    {
        // SSA needs a training series longer than twice its 7-day window.
        var sleep = Daily(BiometricType.SleepEfficiency, Wobble(80, days));
        var strain = Daily(BiometricType.StrainScore, Wobble(30, days));

        var forecast = await _service.ForecastRecoveryAsync(sleep, strain, horizonDays: 7);

        Assert.Empty(forecast);
    }

    [Fact]
    public async Task ForecastRecovery_FifteenDays_IsEnough()
    {
        var sleep = Daily(BiometricType.SleepEfficiency, Wobble(80, 15));
        var strain = Daily(BiometricType.StrainScore, Wobble(30, 15));

        var forecast = await _service.ForecastRecoveryAsync(sleep, strain, horizonDays: 7);

        Assert.Equal(7, forecast.Count);
    }

    [Fact]
    public async Task ForecastRecovery_GapsAndNaN_DoNotCrash()
    {
        var sleepValues = Wobble(80, 30);
        sleepValues[4] = double.NaN;
        sleepValues[12] = double.NaN;
        var sleep = Daily(BiometricType.SleepEfficiency, sleepValues)
            .Where((_, day) => day is not (7 or 8 or 9 or 20)) // missing days
            .ToList();
        var strainValues = Wobble(30, 30);
        strainValues[15] = double.NegativeInfinity;
        var strain = Daily(BiometricType.StrainScore, strainValues);

        var forecast = await _service.ForecastRecoveryAsync(sleep, strain, horizonDays: 7);

        Assert.Equal(7, forecast.Count);
        Assert.All(forecast, p => Assert.False(float.IsNaN(p.PredictedReadiness)));
        Assert.All(forecast, p => Assert.InRange(p.PredictedReadiness, 70f, 82f));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A level with a small deterministic wobble (+-1), like a resting reading.</summary>
    private static double[] Wobble(double level, int count) =>
        Enumerable.Range(0, count).Select(i => level + (i % 3) - 1).ToArray();

    private static List<BiometricEvent> Series(
        BiometricType type, double[] values, TimeSpan step, DateTimeOffset? start = null)
    {
        var t0 = start ?? Start;
        return values.Select((v, i) => Event(type, t0 + step * i, v)).ToList();
    }

    private static List<BiometricEvent> Daily(BiometricType type, double[] values) =>
        Series(type, values, TimeSpan.FromDays(1), Start.AddHours(8));

    private static BiometricEvent Event(BiometricType type, DateTimeOffset ts, double value) =>
        new(Guid.NewGuid(), ts, type, value, "unit", new SourceMetadata("dev-1", "Test", null, 0.9f, ts));
}
