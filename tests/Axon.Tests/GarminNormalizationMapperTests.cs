using Axon.Core.Domain;
using Axon.Infrastructure.Drivers.Garmin;

namespace Axon.Tests;

/// <summary>
/// DoD coverage for GarminNormalizationMapper — unit conversions, optional fields,
/// epoch→DateTimeOffset conversion, and negative stress guard.
/// </summary>
public class GarminNormalizationMapperTests
{
    // Unix epoch 2026-01-15T00:00:00Z = 1768521600
    private const long EpochBase = 1_768_521_600L;

    private static GarminDailySummary MakeDailySummary(
        int? steps = 8000,
        float? activeKcal = 450f,
        float? bmrKcal = 1900f,
        int? avgHr = 72,
        int? maxHr = 155,
        int? rhr = 52,
        float? spo2 = 97.5f,
        int? stress = 35,
        int offsetSec = 0) =>
        new("user1", "token1", EpochBase, EpochBase + 86400, "sum-1", "ACTIVITY",
            EpochBase, offsetSec, 86400,
            steps, null, null, activeKcal, bmrKcal, null, avgHr, maxHr, rhr, null,
            stress, null, null, null, spo2, null);

    private static GarminSleepSummary MakeSleepSummary(
        long durationSec = 27000,
        long? deepSec = 5400,
        long? lightSec = 10800,
        long? remSec = 7200,
        float? spo2 = 96.5f,
        float? resp = 14.3f,
        int? stress = 20) =>
        new("user1", "sleep-1", "2026-01-15", EpochBase, 0, durationSec,
            null, deepSec, lightSec, remSec, null, null, null, null, null, spo2,
            null, null, resp, null, null, stress, null);

    private static GarminHrvSummary MakeHrvSummary(
        int? lastNightAvg = 55,
        GarminHrvReading[]? readings = null) =>
        new("user1", "hrv-1", "2026-01-15", EpochBase,
            new GarminHrvLastNight(60, lastNightAvg, 58, null),
            readings);

    private static GarminBodyComposition MakeBodyComp(
        int? weightG = 80_000,
        int? muscleG = 35_000,
        int? boneG = 3_500,
        float? fatPct = 18f,
        float? waterPct = 55f) =>
        new("user1", "body-1", EpochBase, 0, muscleG, boneG, waterPct, fatPct, null, weightG);

    // ── MapDailySummary ───────────────────────────────────────────────────────

    [Fact]
    public void MapDailySummary_Steps_EmittedWithStepsUnit()
    {
        var events = GarminNormalizationMapper.MapDailySummary(MakeDailySummary(steps: 10_000)).ToList();
        var steps = events.Single(e => e.Type == BiometricType.Steps);

        Assert.Equal(10_000, steps.Value, precision: 0);
        Assert.Equal("steps", steps.Unit);
    }

    [Fact]
    public void MapDailySummary_NullSteps_NotEmitted()
    {
        var events = GarminNormalizationMapper.MapDailySummary(MakeDailySummary(steps: null)).ToList();
        Assert.DoesNotContain(events, e => e.Type == BiometricType.Steps);
    }

    [Fact]
    public void MapDailySummary_ActiveAndBasalKcal_Emitted()
    {
        var events = GarminNormalizationMapper.MapDailySummary(
            MakeDailySummary(activeKcal: 500f, bmrKcal: 1800f)).ToList();

        Assert.Equal(500.0, events.Single(e => e.Type == BiometricType.ActiveEnergyBurned).Value, precision: 1);
        Assert.Equal(1800.0, events.Single(e => e.Type == BiometricType.BasalEnergyBurned).Value, precision: 1);
    }

    [Fact]
    public void MapDailySummary_AllHrMetrics_Emitted()
    {
        var events = GarminNormalizationMapper.MapDailySummary(
            MakeDailySummary(avgHr: 68, maxHr: 152, rhr: 48)).ToList();

        Assert.Equal(68, events.Single(e => e.Type == BiometricType.HeartRate).Value, precision: 0);
        Assert.Equal(152, events.Single(e => e.Type == BiometricType.MaxHeartRate).Value, precision: 0);
        Assert.Equal(48, events.Single(e => e.Type == BiometricType.RestingHeartRate).Value, precision: 0);

        Assert.Equal("bpm", events.First(e => e.Type == BiometricType.HeartRate).Unit);
    }

    [Fact]
    public void MapDailySummary_SpO2_EmittedWithPercentUnit()
    {
        var events = GarminNormalizationMapper.MapDailySummary(MakeDailySummary(spo2: 98.1f)).ToList();
        var spo2 = events.Single(e => e.Type == BiometricType.SpO2);

        Assert.Equal(98.1, spo2.Value, precision: 1);
        Assert.Equal("%", spo2.Unit);
    }

    [Fact]
    public void MapDailySummary_NegativeStress_NotEmitted()
    {
        // Garmin returns -1 for "no stress data"; must be filtered
        var events = GarminNormalizationMapper.MapDailySummary(MakeDailySummary(stress: -1)).ToList();
        Assert.DoesNotContain(events, e => e.Type == BiometricType.StrainScore);
    }

    [Fact]
    public void MapDailySummary_ZeroStress_Emitted()
    {
        var events = GarminNormalizationMapper.MapDailySummary(MakeDailySummary(stress: 0)).ToList();
        // stress >= 0 is valid
        Assert.Contains(events, e => e.Type == BiometricType.StrainScore);
    }

    [Fact]
    public void MapDailySummary_TimestampReflectsEpochPlusOffset()
    {
        // offset = 3600 (UTC+1) — timestamp should have +01:00 offset
        var events = GarminNormalizationMapper.MapDailySummary(
            MakeDailySummary(offsetSec: 3600)).ToList();

        var first = events.First();
        Assert.Equal(TimeSpan.FromHours(1), first.Timestamp.Offset);
    }

    [Fact]
    public void MapDailySummary_Vendor_IsGarmin()
    {
        var events = GarminNormalizationMapper.MapDailySummary(MakeDailySummary()).ToList();
        Assert.All(events, e => Assert.Equal("Garmin", e.Source.Vendor));
    }

    // ── MapSleepSummary ───────────────────────────────────────────────────────

    [Fact]
    public void MapSleepSummary_AlwaysEmitsSleepDuration()
    {
        var events = GarminNormalizationMapper.MapSleepSummary(MakeSleepSummary(durationSec: 25200)).ToList();
        var dur = events.Single(e => e.Type == BiometricType.SleepDuration);

        Assert.Equal(25200, dur.Value, precision: 0);
        Assert.Equal("s", dur.Unit);
    }

    [Fact]
    public void MapSleepSummary_SleepStages_AllInSeconds()
    {
        var events = GarminNormalizationMapper.MapSleepSummary(
            MakeSleepSummary(deepSec: 5400, lightSec: 10800, remSec: 7200)).ToList();

        Assert.Equal(5400, events.Single(e => e.Type == BiometricType.DeepSleepDuration).Value, precision: 0);
        Assert.Equal(10800, events.Single(e => e.Type == BiometricType.LightSleepDuration).Value, precision: 0);
        Assert.Equal(7200, events.Single(e => e.Type == BiometricType.RemDuration).Value, precision: 0);
    }

    [Fact]
    public void MapSleepSummary_NegativeStressDuringSleep_Excluded()
    {
        var events = GarminNormalizationMapper.MapSleepSummary(MakeSleepSummary(stress: -1)).ToList();
        Assert.DoesNotContain(events, e => e.Type == BiometricType.StrainScore);
    }

    // ── MapHrvSummary ─────────────────────────────────────────────────────────

    [Fact]
    public void MapHrvSummary_LastNightAverage_EmittedAsHrv()
    {
        var events = GarminNormalizationMapper.MapHrvSummary(MakeHrvSummary(lastNightAvg: 55)).ToList();
        var hrv = events.First(e => e.Type == BiometricType.HeartRateVariability);

        Assert.Equal(55, hrv.Value, precision: 0);
        Assert.Equal("ms", hrv.Unit);
    }

    [Fact]
    public void MapHrvSummary_NullLastNight_NoAggregateEvent()
    {
        var summary = new GarminHrvSummary("user1", "hrv-1", "2026-01-15", EpochBase,
            LastNight: null, HrvReadings: null);

        var events = GarminNormalizationMapper.MapHrvSummary(summary).ToList();
        Assert.Empty(events);
    }

    [Fact]
    public void MapHrvSummary_GranularReadings_OneEventPerReading()
    {
        var readings = new GarminHrvReading[]
        {
            new(58, EpochBase + 300),
            new(62, EpochBase + 600),
            new(55, EpochBase + 900)
        };

        var events = GarminNormalizationMapper.MapHrvSummary(
            MakeHrvSummary(lastNightAvg: 58, readings: readings)).ToList();

        // 1 aggregate + 3 reading events
        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Equal(BiometricType.HeartRateVariability, e.Type));
    }

    [Fact]
    public void MapHrvSummary_GranularReadings_CorrectTimestamps()
    {
        var readings = new GarminHrvReading[]
        {
            new(50, EpochBase + 300),
            new(55, EpochBase + 600),
        };

        var events = GarminNormalizationMapper.MapHrvSummary(
            MakeHrvSummary(readings: readings)).ToList();

        // Reading events should have timestamps based on their own epochs (offset=0)
        var readingEvents = events.Where(e => e.Timestamp != DateTimeOffset.FromUnixTimeSeconds(EpochBase)).ToList();
        Assert.Equal(2, readingEvents.Count);
    }

    // ── MapBodyComposition ────────────────────────────────────────────────────

    [Fact]
    public void MapBodyComposition_WeightGramsConvertsToKg()
    {
        var events = GarminNormalizationMapper.MapBodyComposition(MakeBodyComp(weightG: 80_000)).ToList();
        var weight = events.Single(e => e.Type == BiometricType.BodyWeight);

        Assert.Equal(80.0, weight.Value, precision: 3);
        Assert.Equal("kg", weight.Unit);
    }

    [Fact]
    public void MapBodyComposition_MuscleGramsConvertsToKg()
    {
        var events = GarminNormalizationMapper.MapBodyComposition(MakeBodyComp(muscleG: 35_000)).ToList();
        var muscle = events.Single(e => e.Type == BiometricType.MuscleMass);

        Assert.Equal(35.0, muscle.Value, precision: 3);
        Assert.Equal("kg", muscle.Unit);
    }

    [Fact]
    public void MapBodyComposition_BoneGramsConvertsToKg()
    {
        var events = GarminNormalizationMapper.MapBodyComposition(MakeBodyComp(boneG: 3_500)).ToList();
        var bone = events.Single(e => e.Type == BiometricType.BoneMass);

        Assert.Equal(3.5, bone.Value, precision: 3);
    }

    [Fact]
    public void MapBodyComposition_BodyFatPercent_PassthroughAsIs()
    {
        var events = GarminNormalizationMapper.MapBodyComposition(MakeBodyComp(fatPct: 20f)).ToList();
        var fat = events.Single(e => e.Type == BiometricType.BodyFatPercentage);

        Assert.Equal(20.0, fat.Value, precision: 3);
        Assert.Equal("%", fat.Unit);
    }

    [Fact]
    public void MapBodyComposition_Hydration_PassthroughAsIs()
    {
        var events = GarminNormalizationMapper.MapBodyComposition(MakeBodyComp(waterPct: 58f)).ToList();
        var hydration = events.Single(e => e.Type == BiometricType.Hydration);

        Assert.Equal(58.0, hydration.Value, precision: 3);
    }

    [Fact]
    public void MapBodyComposition_NullFields_Excluded()
    {
        var events = GarminNormalizationMapper.MapBodyComposition(
            MakeBodyComp(weightG: null, muscleG: null, boneG: null, fatPct: null, waterPct: null)).ToList();

        Assert.Empty(events);
    }

    [Fact]
    public void MapBodyComposition_DeterministicId_SameInputSameId()
    {
        var comp = MakeBodyComp();
        var e1 = GarminNormalizationMapper.MapBodyComposition(comp).ToList();
        var e2 = GarminNormalizationMapper.MapBodyComposition(comp).ToList();

        for (int i = 0; i < e1.Count; i++)
            Assert.Equal(e1[i].Id, e2[i].Id);
    }
}
