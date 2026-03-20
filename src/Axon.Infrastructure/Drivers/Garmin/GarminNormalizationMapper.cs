using Axon.Core.Domain;
using Axon.Infrastructure.Drivers;

namespace Axon.Infrastructure.Drivers.Garmin;

/// <summary>
/// Maps Garmin Health API v1 response models to the Axon Common Schema (ACS).
///
/// Unit-conversion reference
/// ──────────────────────────
///   Garmin Field                        Garmin Unit   ACS Value           ACS Unit
///   ─────────────────────────────────── ──────────── ─────────────────── ─────────
///   steps                               count         as-is               steps
///   activeKilocalories                  kcal          as-is               kcal
///   bmrKilocalories                     kcal          as-is               kcal
///   averageHeartRateInBeatsPerMinute    bpm           as-is               bpm
///   maxHeartRateInBeatsPerMinute        bpm           as-is               bpm
///   restingHeartRateInBeatsPerMinute    bpm           as-is               bpm
///   averageSpO2Value                    % [0,100]    as-is               %
///   averageRespirationValue             breaths/min   as-is               breaths/min
///   averageStressLevel                  Garmin stress [0,100]  as-is      score
///   sleep durations                     seconds       as-is               s
///   HRV readings                        ms (RMSSD)    as-is               ms
///   weightInGrams                       g             ÷ 1000              kg
///   muscleMassInGrams                   g             ÷ 1000              kg
///   boneMassInGrams                     g             ÷ 1000              kg
///   bodyFatInPercent                    % [0,100]    as-is               %
///
/// All methods are static and pure — no side effects, no I/O.
/// 100% unit-test coverage is required (DoD) before any merge.
/// </summary>
public static class GarminNormalizationMapper
{
    private const string Vendor     = "Garmin";
    private const float  Confidence = 0.90f;

    // ── Daily Summary ─────────────────────────────────────────────────────────

    /// <summary>
    /// Expands a <see cref="GarminDailySummary"/> into individual
    /// <see cref="BiometricEvent"/> records — one per present metric.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapDailySummary(
        GarminDailySummary summary,
        string?            correlationId = null)
    {
        var deviceId = summary.SummaryId;
        var ts       = EpochToOffset(summary.StartTimeInSeconds, summary.StartTimeOffsetInSeconds);

        if (summary.Steps.HasValue)
            yield return Make(deviceId, ts, BiometricType.Steps,
                summary.Steps.Value, "steps", correlationId);

        if (summary.ActiveKilocalories.HasValue)
            yield return Make(deviceId, ts, BiometricType.ActiveEnergyBurned,
                summary.ActiveKilocalories.Value, "kcal", correlationId);

        if (summary.BmrKilocalories.HasValue)
            yield return Make(deviceId, ts, BiometricType.BasalEnergyBurned,
                summary.BmrKilocalories.Value, "kcal", correlationId);

        if (summary.AverageHeartRateInBeatsPerMinute.HasValue)
            yield return Make(deviceId, ts, BiometricType.HeartRate,
                summary.AverageHeartRateInBeatsPerMinute.Value, "bpm", correlationId);

        if (summary.MaxHeartRateInBeatsPerMinute.HasValue)
            yield return Make(deviceId, ts, BiometricType.MaxHeartRate,
                summary.MaxHeartRateInBeatsPerMinute.Value, "bpm", correlationId);

        if (summary.RestingHeartRateInBeatsPerMinute.HasValue)
            yield return Make(deviceId, ts, BiometricType.RestingHeartRate,
                summary.RestingHeartRateInBeatsPerMinute.Value, "bpm", correlationId);

        if (summary.AverageSpO2Value.HasValue)
            yield return Make(deviceId, ts, BiometricType.SpO2,
                summary.AverageSpO2Value.Value, "%", correlationId);

        if (summary.AverageStressLevel.HasValue && summary.AverageStressLevel.Value >= 0)
            yield return Make(deviceId, ts, BiometricType.StrainScore,
                summary.AverageStressLevel.Value, "score", correlationId);
    }

    // ── Sleep Summary ─────────────────────────────────────────────────────────

    /// <summary>
    /// Expands a <see cref="GarminSleepSummary"/> into individual
    /// <see cref="BiometricEvent"/> records — sleep stage durations, SpO2, and respiration.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapSleepSummary(
        GarminSleepSummary summary,
        string?            correlationId = null)
    {
        var deviceId = summary.SummaryId;
        var ts       = EpochToOffset(summary.StartTimeInSeconds, summary.StartTimeOffsetInSeconds);

        yield return Make(deviceId, ts, BiometricType.SleepDuration,
            summary.DurationInSeconds, "s", correlationId);

        if (summary.DeepSleepDurationInSeconds.HasValue)
            yield return Make(deviceId, ts, BiometricType.DeepSleepDuration,
                summary.DeepSleepDurationInSeconds.Value, "s", correlationId);

        if (summary.LightSleepDurationInSeconds.HasValue)
            yield return Make(deviceId, ts, BiometricType.LightSleepDuration,
                summary.LightSleepDurationInSeconds.Value, "s", correlationId);

        if (summary.RemSleepInSeconds.HasValue)
            yield return Make(deviceId, ts, BiometricType.RemDuration,
                summary.RemSleepInSeconds.Value, "s", correlationId);

        if (summary.AverageSpO2Value.HasValue)
            yield return Make(deviceId, ts, BiometricType.SpO2,
                summary.AverageSpO2Value.Value, "%", correlationId);

        if (summary.AverageRespirationValue.HasValue)
            yield return Make(deviceId, ts, BiometricType.RespiratoryRate,
                summary.AverageRespirationValue.Value, "breaths/min", correlationId);

        if (summary.AverageStressLevel.HasValue && summary.AverageStressLevel.Value >= 0)
            yield return Make(deviceId, ts, BiometricType.StrainScore,
                summary.AverageStressLevel.Value, "score", correlationId);
    }

    // ── HRV Summary ───────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="GarminHrvSummary"/> to HRV events.
    /// Emits one aggregate event from <c>lastNight</c> and one event per
    /// 5-minute HRV reading if <c>HrvReadings</c> is present.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapHrvSummary(
        GarminHrvSummary summary,
        string?          correlationId = null)
    {
        var deviceId = summary.SummaryId;
        var baseTs   = EpochToOffset(summary.StartTimeInSeconds, 0);

        if (summary.LastNight?.LastNightAverage.HasValue == true)
            yield return Make(deviceId, baseTs, BiometricType.HeartRateVariability,
                summary.LastNight.LastNightAverage!.Value, "ms", correlationId);

        if (summary.HrvReadings is not null)
        {
            foreach (var reading in summary.HrvReadings)
            {
                var ts = EpochToOffset(reading.StartTimeInSeconds, 0);
                yield return Make(deviceId, ts, BiometricType.HeartRateVariability,
                    reading.Hrv, "ms", correlationId);
            }
        }
    }

    // ── Body Composition ──────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="GarminBodyComposition"/> into individual
    /// <see cref="BiometricEvent"/> records for each present measurement.
    /// Garmin reports mass in grams; ACS canonical unit is kilograms.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapBodyComposition(
        GarminBodyComposition composition,
        string?               correlationId = null)
    {
        var deviceId = composition.SummaryId;
        var ts       = EpochToOffset(composition.MeasurementTimeInSeconds,
                                     composition.MeasurementTimeOffset);

        if (composition.WeightInGrams.HasValue)
            yield return Make(deviceId, ts, BiometricType.BodyWeight,
                composition.WeightInGrams.Value / 1000.0, "kg", correlationId);

        if (composition.MuscleMassInGrams.HasValue)
            yield return Make(deviceId, ts, BiometricType.MuscleMass,
                composition.MuscleMassInGrams.Value / 1000.0, "kg", correlationId);

        if (composition.BoneMassInGrams.HasValue)
            yield return Make(deviceId, ts, BiometricType.BoneMass,
                composition.BoneMassInGrams.Value / 1000.0, "kg", correlationId);

        if (composition.BodyFatInPercent.HasValue)
            yield return Make(deviceId, ts, BiometricType.BodyFatPercentage,
                composition.BodyFatInPercent.Value, "%", correlationId);

        if (composition.BodyWaterInPercent.HasValue)
            yield return Make(deviceId, ts, BiometricType.Hydration,
                composition.BodyWaterInPercent.Value, "%", correlationId);
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

    /// <summary>
    /// Converts a Unix epoch (seconds) + UTC offset (seconds) to a
    /// <see cref="DateTimeOffset"/> in the device's local time zone offset.
    /// </summary>
    private static DateTimeOffset EpochToOffset(long epochSeconds, int offsetSeconds)
    {
        var utc    = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        var offset = TimeSpan.FromSeconds(offsetSeconds);
        return utc.ToOffset(offset);
    }
}
