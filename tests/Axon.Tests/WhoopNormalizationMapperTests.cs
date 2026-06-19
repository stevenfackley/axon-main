using Axon.Core.Domain;
using Axon.Infrastructure.Drivers.Whoop;

namespace Axon.Tests;

/// <summary>
/// 100% DoD coverage for WhoopNormalizationMapper (pure static, no I/O).
/// Tests unit conversions, null-score guard, optional fields, and ID determinism.
/// </summary>
public class WhoopNormalizationMapperTests
{
    // ── Shared fixtures ───────────────────────────────────────────────────────

    private static WhoopRecovery MakeRecovery(
        float recovery = 75f,
        float rhr = 52f,
        float hrv = 68f,
        float? spo2 = null,
        float? skin = null,
        string createdAt = "2026-01-15T07:00:00.000Z") =>
        new(1, 100, 200, 42, createdAt, createdAt, "SCORED",
            new WhoopRecoveryScore(false, recovery, rhr, hrv, spo2, skin));

    private static WhoopSleep MakeFullSleep(
        long lightMs = 7_200_000,
        long deepMs = 3_600_000,
        long remMs = 5_400_000,
        long inBedMs = 30_000_000,
        long awakeMs = 3_600_000,
        long noDataMs = 0,
        float? efficiency = 88f,
        float? respRate = 15.2f,
        string start = "2026-01-15T00:30:00.000Z") =>
        new(1, 42, start, start, start, "2026-01-15T07:30:00.000Z", false, "SCORED",
            new WhoopSleepScore(
                new WhoopSleepStageSummary(inBedMs, awakeMs, noDataMs, lightMs, deepMs, remMs, 3, 2),
                null, respRate, null, null, efficiency));

    private static WhoopCycle MakeCycle(
        float strain = 14.2f,
        float kj = 1500f,
        int avgHr = 95,
        int maxHr = 165,
        string start = "2026-01-15T06:00:00.000Z") =>
        new(1, 42, start, start, start, null, "SCORED",
            new WhoopCycleScore(strain, kj, avgHr, maxHr));

    private static WhoopWorkout MakeWorkout(
        float strain = 10.5f,
        float kj = 2100f,
        int avgHr = 145,
        int maxHr = 180,
        string start = "2026-01-15T09:00:00.000Z") =>
        new(1, 42, start, start, start, "2026-01-15T10:00:00.000Z", 0, "SCORED",
            new WhoopWorkoutScore(strain, avgHr, maxHr, kj, 100f, null, null, null, null));

    // ── MapRecovery ───────────────────────────────────────────────────────────

    [Fact]
    public void MapRecovery_NullScore_YieldsNoEvents()
    {
        var recovery = new WhoopRecovery(1, 100, 200, 42,
            "2026-01-15T07:00:00.000Z", "2026-01-15T07:00:00.000Z",
            "PENDING_SLEEP", Score: null);

        var events = WhoopNormalizationMapper.MapRecovery(recovery, "device-1").ToList();

        Assert.Empty(events);
    }

    [Fact]
    public void MapRecovery_WithScore_EmitsThreeRequiredEvents()
    {
        var events = WhoopNormalizationMapper.MapRecovery(MakeRecovery(), "dev1").ToList();

        // Always: RecoveryScore + RestingHeartRate + HRV
        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.Type == BiometricType.RecoveryScore);
        Assert.Contains(events, e => e.Type == BiometricType.RestingHeartRate);
        Assert.Contains(events, e => e.Type == BiometricType.HeartRateVariability);
    }

    [Fact]
    public void MapRecovery_OptionalSpo2_EmittedWhenPresent()
    {
        var events = WhoopNormalizationMapper.MapRecovery(MakeRecovery(spo2: 97.5f), "dev1").ToList();

        var spo2 = events.Single(e => e.Type == BiometricType.SpO2);
        Assert.Equal(97.5, spo2.Value, precision: 3);
        Assert.Equal("%", spo2.Unit);
    }

    [Fact]
    public void MapRecovery_OptionalSpo2_AbsentWhenNull()
    {
        var events = WhoopNormalizationMapper.MapRecovery(MakeRecovery(spo2: null), "dev1").ToList();

        Assert.DoesNotContain(events, e => e.Type == BiometricType.SpO2);
    }

    [Fact]
    public void MapRecovery_OptionalSkinTemp_EmittedWhenPresent()
    {
        var events = WhoopNormalizationMapper.MapRecovery(MakeRecovery(skin: 36.5f), "dev1").ToList();

        var temp = events.Single(e => e.Type == BiometricType.SkinTemperature);
        Assert.Equal(36.5, temp.Value, precision: 3);
        Assert.Equal("°C", temp.Unit);
    }

    [Fact]
    public void MapRecovery_RecoveryScore_CorrectValueAndUnit()
    {
        var events = WhoopNormalizationMapper.MapRecovery(MakeRecovery(recovery: 82f), "dev1").ToList();
        var score = events.Single(e => e.Type == BiometricType.RecoveryScore);

        Assert.Equal(82.0, score.Value, precision: 3);
        Assert.Equal("%", score.Unit);
    }

    [Fact]
    public void MapRecovery_Hrv_CorrectValueAndUnit()
    {
        var events = WhoopNormalizationMapper.MapRecovery(MakeRecovery(hrv: 72.5f), "dev1").ToList();
        var hrv = events.Single(e => e.Type == BiometricType.HeartRateVariability);

        Assert.Equal(72.5, hrv.Value, precision: 3);
        Assert.Equal("ms", hrv.Unit);
    }

    [Fact]
    public void MapRecovery_CorrelationId_Propagated()
    {
        var events = WhoopNormalizationMapper.MapRecovery(MakeRecovery(), "dev1", correlationId: "batch-99").ToList();

        Assert.All(events, e => Assert.Equal("batch-99", e.CorrelationId));
    }

    [Fact]
    public void MapRecovery_Vendor_IsWhoop()
    {
        var events = WhoopNormalizationMapper.MapRecovery(MakeRecovery(), "dev1").ToList();

        Assert.All(events, e => Assert.Equal("Whoop", e.Source.Vendor));
    }

    [Fact]
    public void MapRecovery_DeterministicId_SameInputSameId()
    {
        var r = MakeRecovery();
        var evts1 = WhoopNormalizationMapper.MapRecovery(r, "dev1").ToList();
        var evts2 = WhoopNormalizationMapper.MapRecovery(r, "dev1").ToList();

        for (int i = 0; i < evts1.Count; i++)
            Assert.Equal(evts1[i].Id, evts2[i].Id);
    }

    [Fact]
    public void MapRecovery_DeterministicId_DifferentDeviceDifferentId()
    {
        var r = MakeRecovery();
        var devA = WhoopNormalizationMapper.MapRecovery(r, "devA").ToList();
        var devB = WhoopNormalizationMapper.MapRecovery(r, "devB").ToList();

        for (int i = 0; i < devA.Count; i++)
            Assert.NotEqual(devA[i].Id, devB[i].Id);
    }

    // ── MapSleep ──────────────────────────────────────────────────────────────

    [Fact]
    public void MapSleep_NullScore_YieldsNoEvents()
    {
        var sleep = new WhoopSleep(1, 42, "2026-01-15T00:30:00.000Z", "2026-01-15T07:30:00.000Z",
            "2026-01-15T00:30:00.000Z", "2026-01-15T07:30:00.000Z", false, "SCORED", Score: null);

        var events = WhoopNormalizationMapper.MapSleep(sleep, "dev1").ToList();

        Assert.Empty(events);
    }

    [Fact]
    public void MapSleep_StageDurations_ConvertedMillisToSeconds()
    {
        var events = WhoopNormalizationMapper.MapSleep(MakeFullSleep(
            lightMs: 7_200_000,   // 2h → 7200 s
            deepMs: 3_600_000,    // 1h → 3600 s
            remMs: 5_400_000      // 1.5h → 5400 s
        ), "dev1").ToList();

        Assert.Equal(7200.0, events.Single(e => e.Type == BiometricType.LightSleepDuration).Value, precision: 1);
        Assert.Equal(3600.0, events.Single(e => e.Type == BiometricType.DeepSleepDuration).Value, precision: 1);
        Assert.Equal(5400.0, events.Single(e => e.Type == BiometricType.RemDuration).Value, precision: 1);
    }

    [Fact]
    public void MapSleep_SleepDuration_ExcludesAwakeAndNoData()
    {
        // inBed=30000s, awake=3600s, noData=0 → sleep = (30000-3600-0)/1000 = 26400s
        var events = WhoopNormalizationMapper.MapSleep(MakeFullSleep(
            inBedMs: 30_000_000, awakeMs: 3_600_000, noDataMs: 0
        ), "dev1").ToList();

        Assert.Equal(26400.0, events.Single(e => e.Type == BiometricType.SleepDuration).Value, precision: 1);
    }

    [Fact]
    public void MapSleep_StageDurations_AllInSeconds()
    {
        var events = WhoopNormalizationMapper.MapSleep(MakeFullSleep(), "dev1").ToList();

        var stages = events.Where(e =>
            e.Type is BiometricType.SleepDuration or BiometricType.LightSleepDuration
                or BiometricType.DeepSleepDuration or BiometricType.RemDuration).ToList();

        Assert.All(stages, e => Assert.Equal("s", e.Unit));
    }

    [Fact]
    public void MapSleep_Efficiency_EmittedWithPercentUnit()
    {
        var events = WhoopNormalizationMapper.MapSleep(MakeFullSleep(efficiency: 91f), "dev1").ToList();
        var eff = events.Single(e => e.Type == BiometricType.SleepEfficiency);

        Assert.Equal(91.0, eff.Value, precision: 3);
        Assert.Equal("%", eff.Unit);
    }

    [Fact]
    public void MapSleep_RespiratoryRate_EmittedWithCorrectUnit()
    {
        var events = WhoopNormalizationMapper.MapSleep(MakeFullSleep(respRate: 14.5f), "dev1").ToList();
        var rr = events.Single(e => e.Type == BiometricType.RespiratoryRate);

        Assert.Equal(14.5, rr.Value, precision: 3);
        Assert.Equal("breaths/min", rr.Unit);
    }

    // ── MapCycle ──────────────────────────────────────────────────────────────

    [Fact]
    public void MapCycle_NullScore_YieldsNoEvents()
    {
        var cycle = new WhoopCycle(1, 42, "2026-01-15T06:00:00.000Z", "2026-01-15T06:00:00.000Z",
            "2026-01-15T06:00:00.000Z", null, "SCORED", Score: null);

        Assert.Empty(WhoopNormalizationMapper.MapCycle(cycle, "dev1").ToList());
    }

    [Fact]
    public void MapCycle_KilojouleConvertsToKcal()
    {
        // 1500 kJ × 0.239006 = 358.509 kcal
        var events = WhoopNormalizationMapper.MapCycle(MakeCycle(kj: 1500f), "dev1").ToList();
        var energy = events.Single(e => e.Type == BiometricType.ActiveEnergyBurned);

        Assert.Equal(1500 * 0.239006, energy.Value, precision: 3);
        Assert.Equal("kcal", energy.Unit);
    }

    [Fact]
    public void MapCycle_EmitsFourEvents()
    {
        var events = WhoopNormalizationMapper.MapCycle(MakeCycle(), "dev1").ToList();

        Assert.Equal(4, events.Count);
        Assert.Contains(events, e => e.Type == BiometricType.StrainScore);
        Assert.Contains(events, e => e.Type == BiometricType.ActiveEnergyBurned);
        Assert.Contains(events, e => e.Type == BiometricType.HeartRate);
        Assert.Contains(events, e => e.Type == BiometricType.MaxHeartRate);
    }

    [Fact]
    public void MapCycle_StrainUnit_IsStrain()
    {
        var events = WhoopNormalizationMapper.MapCycle(MakeCycle(strain: 18.1f), "dev1").ToList();
        var strain = events.Single(e => e.Type == BiometricType.StrainScore);

        Assert.Equal(18.1, strain.Value, precision: 3);
        Assert.Equal("strain", strain.Unit);
    }

    // ── MapWorkout ────────────────────────────────────────────────────────────

    [Fact]
    public void MapWorkout_NullScore_YieldsNoEvents()
    {
        var workout = new WhoopWorkout(1, 42, "2026-01-15T09:00:00.000Z", "2026-01-15T09:00:00.000Z",
            "2026-01-15T09:00:00.000Z", "2026-01-15T10:00:00.000Z", 0, "SCORED", Score: null);

        Assert.Empty(WhoopNormalizationMapper.MapWorkout(workout, "dev1").ToList());
    }

    [Fact]
    public void MapWorkout_KilojouleConvertsToKcal()
    {
        var events = WhoopNormalizationMapper.MapWorkout(MakeWorkout(kj: 2100f), "dev1").ToList();
        var energy = events.Single(e => e.Type == BiometricType.ActiveEnergyBurned);

        Assert.Equal(2100 * 0.239006, energy.Value, precision: 3);
    }

    [Fact]
    public void MapWorkout_StrainMapsToTrainingLoad()
    {
        var events = WhoopNormalizationMapper.MapWorkout(MakeWorkout(strain: 12.3f), "dev1").ToList();
        var tl = events.Single(e => e.Type == BiometricType.TrainingLoad);

        Assert.Equal(12.3, tl.Value, precision: 3);
    }

    // ── MapBodyWeight ─────────────────────────────────────────────────────────

    [Fact]
    public void MapBodyWeight_ReturnsBodyWeightEventInKg()
    {
        var body = new WhoopBodyMeasurement(1.75f, 80.5f, 185);
        var ts = DateTimeOffset.UtcNow;

        var evt = WhoopNormalizationMapper.MapBodyWeight(body, "dev1", ts);

        Assert.Equal(BiometricType.BodyWeight, evt.Type);
        Assert.Equal(80.5, evt.Value, precision: 3);
        Assert.Equal("kg", evt.Unit);
        Assert.Equal(ts, evt.Timestamp);
    }

    [Fact]
    public void MapBodyWeight_NullTimestamp_UsesNow()
    {
        var body = new WhoopBodyMeasurement(1.75f, 80.5f, 185);
        var before = DateTimeOffset.UtcNow;

        var evt = WhoopNormalizationMapper.MapBodyWeight(body, "dev1", measuredAt: null);

        Assert.True(evt.Timestamp >= before);
    }
}
