using Axon.Core.Domain;
using Axon.Infrastructure.Drivers.Oura;

namespace Axon.Tests;

/// <summary>
/// DoD coverage for OuraNormalizationMapper — sleep stages, readiness, HR samples,
/// SpO2, activity, time-series expansion, null-gap skipping.
/// </summary>
public class OuraNormalizationMapperTests
{
    // ── MapDailyReadiness ─────────────────────────────────────────────────────

    [Fact]
    public void MapDailyReadiness_Score_EmittedCorrectly()
    {
        var readiness = new OuraDailyReadiness(
            "rid-1", "2026-01-15", Score: 85, TemperatureDeviation: null,
            TemperatureTrendDeviation: null,
            Timestamp: "2026-01-15T00:00:00+00:00", Contributors: null);

        var events = OuraNormalizationMapper.MapDailyReadiness(readiness).ToList();
        var score = events.Single(e => e.Type == BiometricType.ReadinessScore);

        Assert.Equal(85, score.Value, precision: 0);
        Assert.Equal("score", score.Unit);
        Assert.Equal("rid-1", score.Source.DeviceId);
    }

    [Fact]
    public void MapDailyReadiness_NullScore_NotEmitted()
    {
        var readiness = new OuraDailyReadiness("rid-1", "2026-01-15", Score: null,
            TemperatureDeviation: null, TemperatureTrendDeviation: null,
            Timestamp: "2026-01-15T00:00:00+00:00", Contributors: null);

        var events = OuraNormalizationMapper.MapDailyReadiness(readiness).ToList();
        Assert.DoesNotContain(events, e => e.Type == BiometricType.ReadinessScore);
    }

    [Fact]
    public void MapDailyReadiness_TemperatureDeviation_EmittedAsSkinTemp()
    {
        var readiness = new OuraDailyReadiness("rid-1", "2026-01-15", Score: null,
            TemperatureDeviation: 0.3f, TemperatureTrendDeviation: null,
            Timestamp: "2026-01-15T00:00:00+00:00", Contributors: null);

        var events = OuraNormalizationMapper.MapDailyReadiness(readiness).ToList();
        var temp = events.Single(e => e.Type == BiometricType.SkinTemperature);

        Assert.Equal(0.3, temp.Value, precision: 3);
        Assert.Equal("°C", temp.Unit);
    }

    [Fact]
    public void MapDailyReadiness_Vendor_IsOura()
    {
        var readiness = new OuraDailyReadiness("rid-1", "2026-01-15", Score: 75,
            TemperatureDeviation: null, TemperatureTrendDeviation: null,
            Timestamp: "2026-01-15T00:00:00+00:00", Contributors: null);

        var events = OuraNormalizationMapper.MapDailyReadiness(readiness).ToList();
        Assert.All(events, e => Assert.Equal("Oura", e.Source.Vendor));
    }

    // ── MapSleepSession ───────────────────────────────────────────────────────

    private static OuraSleepSession MakeSleep(
        int? totalSec = 25200,
        int? deepSec = 5400,
        int? lightSec = 10800,
        int? remSec = 7200,
        int? efficiency = 88,
        int? latency = 420,
        float? avgHr = 55f,
        int? lowestHr = 48,
        int? avgHrv = 62,
        float? avgBreath = 13.5f,
        OuraTimeSeries? hrSeries = null,
        OuraTimeSeries? hrvSeries = null) =>
        // Parameters match OuraSleepSession positional order:
        // Id, AverageBreath, AverageHeartRate, AverageHrv, AwakeTime,
        // BedtimeEnd, BedtimeStart, Day, DeepSleepDuration, Efficiency,
        // HeartRate, Hrv, Latency, LightSleepDuration, LowBatteryAlert,
        // LowestHeartRate, Movement30Sec, Period, Readiness, ReadinessScoreDelta,
        // RemSleepDuration, RestlessPeriods, SleepPhase5Min, SleepScoreDelta,
        // SleepAlgorithmVersion, TimeInBed, TotalSleepDuration, Type
        new("sess-1", avgBreath, avgHr, avgHrv, null,
            "2026-01-15T07:30:00+00:00",
            "2026-01-15T00:30:00+00:00",
            "2026-01-15", deepSec, efficiency, hrSeries, hrvSeries, latency, lightSec,
            false, lowestHr, null, 0, null, null, remSec, null, null, null, null,
            null, totalSec, "long_sleep");

    [Fact]
    public void MapSleepSession_SleepDuration_EmittedInSeconds()
    {
        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(totalSec: 25200)).ToList();
        var dur = events.Single(e => e.Type == BiometricType.SleepDuration);

        Assert.Equal(25200, dur.Value, precision: 0);
        Assert.Equal("s", dur.Unit);
    }

    [Fact]
    public void MapSleepSession_SleepStages_AllEmitted()
    {
        var events = OuraNormalizationMapper.MapSleepSession(
            MakeSleep(deepSec: 5400, lightSec: 10800, remSec: 7200)).ToList();

        Assert.Equal(5400, events.Single(e => e.Type == BiometricType.DeepSleepDuration).Value, precision: 0);
        Assert.Equal(10800, events.Single(e => e.Type == BiometricType.LightSleepDuration).Value, precision: 0);
        Assert.Equal(7200, events.Single(e => e.Type == BiometricType.RemDuration).Value, precision: 0);
    }

    [Fact]
    public void MapSleepSession_Efficiency_EmittedWithPercentUnit()
    {
        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(efficiency: 91)).ToList();
        var eff = events.Single(e => e.Type == BiometricType.SleepEfficiency);

        Assert.Equal(91, eff.Value, precision: 0);
        Assert.Equal("%", eff.Unit);
    }

    [Fact]
    public void MapSleepSession_AverageHr_MapsToHeartRate()
    {
        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(avgHr: 54f)).ToList();
        // Aggregate HR during sleep (excluding time-series events)
        var hr = events.Where(e => e.Type == BiometricType.HeartRate).ToList();
        Assert.Contains(hr, e => e.Value == 54.0);
    }

    [Fact]
    public void MapSleepSession_LowestHr_MapsToRestingHeartRate()
    {
        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(lowestHr: 46)).ToList();
        var rhr = events.Single(e => e.Type == BiometricType.RestingHeartRate);

        Assert.Equal(46, rhr.Value, precision: 0);
        Assert.Equal("bpm", rhr.Unit);
    }

    [Fact]
    public void MapSleepSession_AverageHrv_EmittedInMs()
    {
        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(avgHrv: 65)).ToList();
        // Aggregate HRV (excluding time-series)
        var hrv = events.Where(e => e.Type == BiometricType.HeartRateVariability).ToList();
        Assert.Contains(hrv, e => e.Value == 65.0);
    }

    [Fact]
    public void MapSleepSession_RespiratoryRate_Emitted()
    {
        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(avgBreath: 14.2f)).ToList();
        var rr = events.Single(e => e.Type == BiometricType.RespiratoryRate);

        Assert.Equal(14.2, rr.Value, precision: 2);
    }

    [Fact]
    public void MapSleepSession_SleepOnsetLatency_Emitted()
    {
        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(latency: 600)).ToList();
        var latency = events.Single(e => e.Type == BiometricType.SleepOnsetLatency);

        Assert.Equal(600, latency.Value, precision: 0);
        Assert.Equal("s", latency.Unit);
    }

    [Fact]
    public void MapSleepSession_HrTimeSeries_ExpandsOneEventPerNonNullSample()
    {
        var hrSeries = new OuraTimeSeries(
            300f,  // 5-minute interval
            new float?[] { 58f, null, 62f, 55f },  // null = gap
            "2026-01-15T01:00:00+00:00");

        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(hrSeries: hrSeries)).ToList();

        // 3 non-null HR time-series + aggregate HR
        var hrEvents = events.Where(e => e.Type == BiometricType.HeartRate).ToList();
        Assert.Equal(4, hrEvents.Count); // 1 aggregate + 3 time-series
    }

    [Fact]
    public void MapSleepSession_HrTimeSeries_NullGapsSkipped()
    {
        var hrSeries = new OuraTimeSeries(
            60f,
            new float?[] { null, null, 60f },
            "2026-01-15T01:00:00+00:00");

        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(hrSeries: hrSeries)).ToList();

        // Only 1 time-series HR event (the non-null one)
        var hrEvents = events.Where(e => e.Type == BiometricType.HeartRate).ToList();
        Assert.Single(hrEvents, e => e.Timestamp == DateTimeOffset.Parse("2026-01-15T01:02:00+00:00"));
    }

    [Fact]
    public void MapSleepSession_HrTimeSeries_TimestampOffsetByInterval()
    {
        // index 0 → +0s, index 1 → +300s, index 2 → +600s
        var hrSeries = new OuraTimeSeries(
            300f,
            new float?[] { 58f, 60f, 62f },
            "2026-01-15T02:00:00+00:00");

        var events = OuraNormalizationMapper.MapSleepSession(MakeSleep(hrSeries: hrSeries)).ToList();

        var tsEvents = events
            .Where(e => e.Type == BiometricType.HeartRate
                && e.Timestamp >= DateTimeOffset.Parse("2026-01-15T02:00:00+00:00"))
            .OrderBy(e => e.Timestamp)
            .ToList();

        Assert.Equal(3, tsEvents.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T02:00:00+00:00"), tsEvents[0].Timestamp);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T02:05:00+00:00"), tsEvents[1].Timestamp);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T02:10:00+00:00"), tsEvents[2].Timestamp);
    }

    // ── Helpers for OuraDailyActivity (26 positional params) ─────────────────
    // Order: Id, Class5Min, Score, ActiveCalories, AverageMetMinutes, Contributors,
    //        EquivalentWalkingDistance, HighActivityMetMinutes, HighActivityTime,
    //        InactivityAlerts, LowActivityMetMinutes, LowActivityTime,
    //        MediumActivityMetMinutes, MediumActivityTime, Met, MetersToTarget,
    //        NonWearTime, RestingTime, SedentaryMetMinutes, SedentaryTime,
    //        Steps, TargetCalories, TargetMeters, TotalCalories, Day, Timestamp

    private static OuraDailyActivity MakeActivity(
        int? steps = null,
        int? activeCalories = null,
        int? totalCalories = null) =>
        new("act-1", null, 75, activeCalories, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null,
            steps, null, null, totalCalories,
            "2026-01-15", "2026-01-15T12:00:00+00:00");

    // ── MapDailyActivity ──────────────────────────────────────────────────────

    [Fact]
    public void MapDailyActivity_Steps_EmittedWithStepsUnit()
    {
        var events = OuraNormalizationMapper.MapDailyActivity(MakeActivity(steps: 9000)).ToList();
        var steps = events.Single(e => e.Type == BiometricType.Steps);

        Assert.Equal(9000, steps.Value, precision: 0);
        Assert.Equal("steps", steps.Unit);
    }

    [Fact]
    public void MapDailyActivity_ActiveCalories_EmittedCorrectly()
    {
        var events = OuraNormalizationMapper.MapDailyActivity(MakeActivity(activeCalories: 500)).ToList();
        var calories = events.Single(e => e.Type == BiometricType.ActiveEnergyBurned);

        Assert.Equal(500, calories.Value, precision: 0);
        Assert.Equal("kcal", calories.Unit);
    }

    [Fact]
    public void MapDailyActivity_BasalCalories_TotalMinusActive()
    {
        // total=2200, active=500 → basal=1700
        var events = OuraNormalizationMapper.MapDailyActivity(
            MakeActivity(activeCalories: 500, totalCalories: 2200)).ToList();
        var basal = events.Single(e => e.Type == BiometricType.BasalEnergyBurned);

        Assert.Equal(1700, basal.Value, precision: 0);
    }

    // ── MapHeartRateSample ────────────────────────────────────────────────────

    [Fact]
    public void MapHeartRateSample_EmitsSingleHeartRateEvent()
    {
        var sample = new OuraHeartRateSample(72, "rest", "2026-01-15T12:00:00+00:00");
        var evt = OuraNormalizationMapper.MapHeartRateSample(sample, "ring-1");

        Assert.Equal(BiometricType.HeartRate, evt.Type);
        Assert.Equal(72, evt.Value, precision: 0);
        Assert.Equal("bpm", evt.Unit);
    }

    [Fact]
    public void MapHeartRateSample_DeviceIdEncodesSrcAndSource()
    {
        var sample = new OuraHeartRateSample(65, "workout", "2026-01-15T13:00:00+00:00");
        var evt = OuraNormalizationMapper.MapHeartRateSample(sample, "ring-abc");

        // DeviceId should be "ring-abc:workout" per mapper spec
        Assert.Equal("ring-abc:workout", evt.Source.DeviceId);
    }

    // ── MapSpO2Daily ──────────────────────────────────────────────────────────

    [Fact]
    public void MapSpO2Daily_ValidAverage_EmitsSpO2Event()
    {
        var spo2 = new OuraSpO2Daily("spo2-1", "2026-01-15",
            new OuraSpo2Percentage(98.5f, 95f, 99.5f));

        var evt = OuraNormalizationMapper.MapSpO2Daily(spo2, "ring-1");

        Assert.NotNull(evt);
        Assert.Equal(BiometricType.SpO2, evt!.Type);
        Assert.Equal(98.5, evt.Value, precision: 1);
        Assert.Equal("%", evt.Unit);
    }

    [Fact]
    public void MapSpO2Daily_NullPercentage_ReturnsNull()
    {
        var spo2 = new OuraSpO2Daily("spo2-1", "2026-01-15", Spo2Percentage: null);
        var evt = OuraNormalizationMapper.MapSpO2Daily(spo2, "ring-1");

        Assert.Null(evt);
    }

    [Fact]
    public void MapSpO2Daily_NullAverage_ReturnsNull()
    {
        var spo2 = new OuraSpO2Daily("spo2-1", "2026-01-15",
            new OuraSpo2Percentage(Average: null, Min: 95f, Max: 99f));

        var evt = OuraNormalizationMapper.MapSpO2Daily(spo2, "ring-1");

        Assert.Null(evt);
    }

    [Fact]
    public void MapSpO2Daily_CorrelationId_Propagated()
    {
        var spo2 = new OuraSpO2Daily("spo2-1", "2026-01-15",
            new OuraSpo2Percentage(97f, 94f, 99f));

        var evt = OuraNormalizationMapper.MapSpO2Daily(spo2, "ring-1", correlationId: "batch-42");

        Assert.Equal("batch-42", evt?.CorrelationId);
    }
}
