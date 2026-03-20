using Axon.Core.Domain;
using Axon.Infrastructure.Drivers;

namespace Axon.Infrastructure.Drivers.Whoop;

/// <summary>
/// Maps Whoop API v1 response models to the Axon Common Schema (ACS).
///
/// Unit-conversion reference
/// ──────────────────────────
///   Whoop Field                    Whoop Unit    ACS Value           ACS Unit
///   ────────────────────────────── ──────────── ───────────────────  ─────────
///   recovery_score                 %  [0,100]   as-is               %
///   resting_heart_rate             bpm           as-is               bpm
///   hrv_rmssd_milli                ms            as-is               ms
///   spo2_percentage                % [0,100]    as-is               %
///   skin_temp_celsius              °C            as-is               °C
///   sleep stage durations          ms            ÷ 1000              s
///   respiratory_rate               breaths/min   as-is               breaths/min
///   sleep_efficiency_percentage    % [0,100]    as-is               %
///   cycle strain                   strain units  as-is               strain
///   cycle kilojoule                kJ            × 0.239006          kcal
///   cycle average_heart_rate       bpm           as-is               bpm
///   cycle max_heart_rate           bpm           as-is               bpm
///   body weight_kilogram           kg            as-is               kg
///
/// All methods are static and pure — no side effects, no I/O.
/// 100% unit-test coverage is required (DoD) before any merge.
/// </summary>
public static class WhoopNormalizationMapper
{
    private const string Vendor   = "Whoop";
    private const float  Confidence = 0.92f;

    // kJ → kcal conversion factor
    private const double KjToKcal = 0.239006;

    // ── Recovery ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Expands a <see cref="WhoopRecovery"/> record into individual
    /// <see cref="BiometricEvent"/> records — one per scored metric.
    /// Returns an empty enumerable if the record has no score
    /// (e.g., <c>score_state</c> is "PENDING_SLEEP").
    /// </summary>
    public static IEnumerable<BiometricEvent> MapRecovery(
        WhoopRecovery recovery,
        string        deviceId,
        string?       correlationId = null)
    {
        var score = recovery.Score;
        if (score is null) yield break;

        // Timestamps for recovery records use the created_at field.
        var ts = ParseTimestamp(recovery.CreatedAt);

        yield return Make(deviceId, ts, BiometricType.RecoveryScore,
            score.RecoveryScore, "%", correlationId);

        yield return Make(deviceId, ts, BiometricType.RestingHeartRate,
            score.RestingHeartRate, "bpm", correlationId);

        yield return Make(deviceId, ts, BiometricType.HeartRateVariability,
            score.HrvRmssdMilli, "ms", correlationId);

        if (score.Spo2Percentage.HasValue)
            yield return Make(deviceId, ts, BiometricType.SpO2,
                score.Spo2Percentage.Value, "%", correlationId);

        if (score.SkinTempCelsius.HasValue)
            yield return Make(deviceId, ts, BiometricType.SkinTemperature,
                score.SkinTempCelsius.Value, "°C", correlationId);
    }

    // ── Sleep ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Expands a <see cref="WhoopSleep"/> record into individual
    /// <see cref="BiometricEvent"/> records — one per sleep metric.
    /// Nap records are included; callers may filter on <see cref="WhoopSleep.IsNap"/>
    /// if nap exclusion is required.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapSleep(
        WhoopSleep sleep,
        string     deviceId,
        string?    correlationId = null)
    {
        var score = sleep.Score;
        if (score is null) yield break;

        var ts = ParseTimestamp(sleep.Start);

        // Sleep stage durations — Whoop reports in milliseconds; ACS wants seconds.
        var stages = score.StageSummary;
        if (stages is not null)
        {
            yield return Make(deviceId, ts, BiometricType.SleepDuration,
                MillisToSeconds(stages.TotalInBedTimeMilli
                    - stages.TotalAwakeTimeMilli
                    - stages.TotalNoDataTimeMilli),
                "s", correlationId);

            yield return Make(deviceId, ts, BiometricType.LightSleepDuration,
                MillisToSeconds(stages.TotalLightSleepTimeMilli), "s", correlationId);

            yield return Make(deviceId, ts, BiometricType.DeepSleepDuration,
                MillisToSeconds(stages.TotalSlowWaveSleepTimeMilli), "s", correlationId);

            yield return Make(deviceId, ts, BiometricType.RemDuration,
                MillisToSeconds(stages.TotalRemSleepTimeMilli), "s", correlationId);
        }

        if (score.SleepEfficiencyPercentage.HasValue)
            yield return Make(deviceId, ts, BiometricType.SleepEfficiency,
                score.SleepEfficiencyPercentage.Value, "%", correlationId);

        if (score.RespiratoryRate.HasValue)
            yield return Make(deviceId, ts, BiometricType.RespiratoryRate,
                score.RespiratoryRate.Value, "breaths/min", correlationId);
    }

    // ── Cycle (Strain) ────────────────────────────────────────────────────────

    /// <summary>
    /// Expands a <see cref="WhoopCycle"/> into individual <see cref="BiometricEvent"/>
    /// records — strain score, energy expenditure, and HR metrics.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapCycle(
        WhoopCycle cycle,
        string     deviceId,
        string?    correlationId = null)
    {
        var score = cycle.Score;
        if (score is null) yield break;

        var ts = ParseTimestamp(cycle.Start);

        yield return Make(deviceId, ts, BiometricType.StrainScore,
            score.Strain, "strain", correlationId);

        // Whoop reports kilojoules; ACS canonical unit for energy is kcal.
        yield return Make(deviceId, ts, BiometricType.ActiveEnergyBurned,
            score.Kilojoule * KjToKcal, "kcal", correlationId);

        yield return Make(deviceId, ts, BiometricType.HeartRate,
            score.AverageHeartRate, "bpm", correlationId);

        yield return Make(deviceId, ts, BiometricType.MaxHeartRate,
            score.MaxHeartRate, "bpm", correlationId);
    }

    // ── Workout ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Expands a <see cref="WhoopWorkout"/> into individual <see cref="BiometricEvent"/>
    /// records — mirrors <see cref="MapCycle"/> but scoped to a single workout activity.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapWorkout(
        WhoopWorkout workout,
        string       deviceId,
        string?      correlationId = null)
    {
        var score = workout.Score;
        if (score is null) yield break;

        var ts = ParseTimestamp(workout.Start);

        yield return Make(deviceId, ts, BiometricType.TrainingLoad,
            score.Strain, "strain", correlationId);

        yield return Make(deviceId, ts, BiometricType.ActiveEnergyBurned,
            score.Kilojoule * KjToKcal, "kcal", correlationId);

        yield return Make(deviceId, ts, BiometricType.HeartRate,
            score.AverageHeartRate, "bpm", correlationId);

        yield return Make(deviceId, ts, BiometricType.MaxHeartRate,
            score.MaxHeartRate, "bpm", correlationId);
    }

    // ── Body Measurement ──────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="WhoopBodyMeasurement"/> snapshot to a weight event.
    /// Uses ingestion time as the timestamp since Whoop does not timestamp body measurements.
    /// </summary>
    public static BiometricEvent MapBodyWeight(
        WhoopBodyMeasurement body,
        string               deviceId,
        DateTimeOffset?      measuredAt    = null,
        string?              correlationId = null)
    {
        var ts = measuredAt ?? DateTimeOffset.UtcNow;
        return Make(deviceId, ts, BiometricType.BodyWeight,
            body.WeightKilogram, "kg", correlationId);
    }

    // ── Private Helpers ───────────────────────────────────────────────────────

    private static BiometricEvent Make(
        string         deviceId,
        DateTimeOffset timestamp,
        BiometricType  type,
        double         value,
        string         unit,
        string?        correlationId) =>
        new(
            Id:            DriverUtilities.DeterministicId(Vendor, deviceId, timestamp, type),
            Timestamp:     timestamp,
            Type:          type,
            Value:         value,
            Unit:          unit,
            Source:        DriverUtilities.BuildSource(Vendor, deviceId, Confidence),
            CorrelationId: correlationId);

    private static DateTimeOffset ParseTimestamp(string iso8601)
        => DateTimeOffset.Parse(iso8601,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

    private static double MillisToSeconds(long millis) => millis / 1000.0;
}
