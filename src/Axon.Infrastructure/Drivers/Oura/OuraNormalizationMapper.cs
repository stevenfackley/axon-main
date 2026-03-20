using Axon.Core.Domain;
using Axon.Infrastructure.Drivers;

namespace Axon.Infrastructure.Drivers.Oura;

/// <summary>
/// Maps Oura Ring API v2 response models to the Axon Common Schema (ACS).
///
/// Unit-conversion reference
/// ──────────────────────────
///   Oura Field                          Oura Unit      ACS Value         ACS Unit
///   ─────────────────────────────────── ────────────── ──────────────── ─────────
///   readiness score                     [0,100]        as-is             score
///   temperature_deviation               °C (delta)     as-is             °C
///   sleep session durations             seconds        as-is             s
///   sleep efficiency                    % [0,100]      as-is             %
///   sleep latency                       seconds        as-is             s
///   average_heart_rate (sleep)          bpm            as-is             bpm
///   lowest_heart_rate (sleep)           bpm            as-is             bpm
///   average_hrv (sleep)                 ms             as-is             ms
///   average_breath (sleep)              breaths/min    as-is             breaths/min
///   daily activity steps                count          as-is             steps
///   active_calories (activity)          kcal           as-is             kcal
///   total_calories (activity)           kcal           as-is             kcal
///   HR samples bpm                      bpm            as-is             bpm
///   SpO2 average percentage             % [0,100]      as-is             %
///
/// All methods are static and pure — no side effects, no I/O.
/// 100% unit-test coverage is required (DoD) before any merge.
/// </summary>
public static class OuraNormalizationMapper
{
    private const string Vendor     = "Oura";
    private const float  Confidence = 0.93f;

    // ── Daily Readiness ───────────────────────────────────────────────────────

    /// <summary>
    /// Expands an <see cref="OuraDailyReadiness"/> record into individual
    /// <see cref="BiometricEvent"/> records.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapDailyReadiness(
        OuraDailyReadiness readiness,
        string?            correlationId = null)
    {
        var ts = ParseTimestamp(readiness.Timestamp);
        // Use the record ID as device identifier (Oura Ring ID is embedded in each record)
        var deviceId = readiness.Id;

        if (readiness.Score.HasValue)
            yield return Make(deviceId, ts, BiometricType.ReadinessScore,
                readiness.Score.Value, "score", correlationId);

        if (readiness.TemperatureDeviation.HasValue)
            yield return Make(deviceId, ts, BiometricType.SkinTemperature,
                readiness.TemperatureDeviation.Value, "°C", correlationId);
    }

    // ── Sleep Session ─────────────────────────────────────────────────────────

    /// <summary>
    /// Expands an <see cref="OuraSleepSession"/> into individual
    /// <see cref="BiometricEvent"/> records — sleep durations, efficiency,
    /// HR, HRV, respiration, and latency.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapSleepSession(
        OuraSleepSession session,
        string?          correlationId = null)
    {
        var ts       = ParseTimestamp(session.BedtimeStart);
        var deviceId = session.Id;

        if (session.TotalSleepDuration.HasValue)
            yield return Make(deviceId, ts, BiometricType.SleepDuration,
                session.TotalSleepDuration.Value, "s", correlationId);

        if (session.DeepSleepDuration.HasValue)
            yield return Make(deviceId, ts, BiometricType.DeepSleepDuration,
                session.DeepSleepDuration.Value, "s", correlationId);

        if (session.LightSleepDuration.HasValue)
            yield return Make(deviceId, ts, BiometricType.LightSleepDuration,
                session.LightSleepDuration.Value, "s", correlationId);

        if (session.RemSleepDuration.HasValue)
            yield return Make(deviceId, ts, BiometricType.RemDuration,
                session.RemSleepDuration.Value, "s", correlationId);

        if (session.Efficiency.HasValue)
            yield return Make(deviceId, ts, BiometricType.SleepEfficiency,
                session.Efficiency.Value, "%", correlationId);

        if (session.Latency.HasValue)
            yield return Make(deviceId, ts, BiometricType.SleepOnsetLatency,
                session.Latency.Value, "s", correlationId);

        if (session.AverageHeartRate.HasValue)
            yield return Make(deviceId, ts, BiometricType.HeartRate,
                session.AverageHeartRate.Value, "bpm", correlationId);

        if (session.LowestHeartRate.HasValue)
            yield return Make(deviceId, ts, BiometricType.RestingHeartRate,
                session.LowestHeartRate.Value, "bpm", correlationId);

        if (session.AverageHrv.HasValue)
            yield return Make(deviceId, ts, BiometricType.HeartRateVariability,
                session.AverageHrv.Value, "ms", correlationId);

        if (session.AverageBreath.HasValue)
            yield return Make(deviceId, ts, BiometricType.RespiratoryRate,
                session.AverageBreath.Value, "breaths/min", correlationId);

        // Expand granular HR time-series embedded in the sleep session
        if (session.HeartRate is { } hrSeries)
        {
            foreach (var evt in MapTimeSeries(hrSeries, deviceId,
                         BiometricType.HeartRate, "bpm", correlationId))
                yield return evt;
        }

        // Expand granular HRV time-series embedded in the sleep session
        if (session.Hrv is { } hrvSeries)
        {
            foreach (var evt in MapTimeSeries(hrvSeries, deviceId,
                         BiometricType.HeartRateVariability, "ms", correlationId))
                yield return evt;
        }
    }

    // ── Daily Activity ────────────────────────────────────────────────────────

    /// <summary>
    /// Expands an <see cref="OuraDailyActivity"/> into individual
    /// <see cref="BiometricEvent"/> records.
    /// </summary>
    public static IEnumerable<BiometricEvent> MapDailyActivity(
        OuraDailyActivity activity,
        string?           correlationId = null)
    {
        var ts       = ParseTimestamp(activity.Timestamp);
        var deviceId = activity.Id;

        if (activity.Steps.HasValue)
            yield return Make(deviceId, ts, BiometricType.Steps,
                activity.Steps.Value, "steps", correlationId);

        if (activity.ActiveCalories.HasValue)
            yield return Make(deviceId, ts, BiometricType.ActiveEnergyBurned,
                activity.ActiveCalories.Value, "kcal", correlationId);

        if (activity.TotalCalories.HasValue)
            yield return Make(deviceId, ts, BiometricType.BasalEnergyBurned,
                activity.TotalCalories.Value - (activity.ActiveCalories ?? 0), "kcal", correlationId);
    }

    // ── Heart Rate Samples ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a continuous <see cref="OuraHeartRateSample"/> to a single
    /// <see cref="BiometricEvent"/>. Source field is preserved in the device ID
    /// to distinguish rest/workout/sleep sampling contexts.
    /// </summary>
    public static BiometricEvent MapHeartRateSample(
        OuraHeartRateSample sample,
        string              ringId,
        string?             correlationId = null)
    {
        var ts       = ParseTimestamp(sample.Timestamp);
        var deviceId = $"{ringId}:{sample.Source}";

        return Make(deviceId, ts, BiometricType.HeartRate,
            sample.Bpm, "bpm", correlationId);
    }

    // ── SpO2 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps the average SpO2 from an <see cref="OuraSpO2Daily"/> record.
    /// Returns <c>null</c> if no average value is present.
    /// </summary>
    public static BiometricEvent? MapSpO2Daily(
        OuraSpO2Daily spO2,
        string        ringId,
        string?       correlationId = null)
    {
        if (spO2.Spo2Percentage?.Average is not { } avg) return null;

        // Use midnight UTC on the calendar date as the canonical timestamp
        var ts = DateTimeOffset.Parse(
            spO2.Day,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);

        return Make(ringId, ts, BiometricType.SpO2, avg, "%", correlationId);
    }

    // ── Private Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Expands an <see cref="OuraTimeSeries"/> into individual <see cref="BiometricEvent"/>
    /// records, one per non-null sample. Null items represent gaps in the time-series
    /// (e.g., wake periods) and are skipped.
    /// </summary>
    private static IEnumerable<BiometricEvent> MapTimeSeries(
        OuraTimeSeries series,
        string         deviceId,
        BiometricType  type,
        string         unit,
        string?        correlationId)
    {
        var startTs    = ParseTimestamp(series.Timestamp);
        var intervalMs = (long)(series.Interval * 1000.0);

        for (int i = 0; i < series.Items.Count; i++)
        {
            var value = series.Items[i];
            if (value is null) continue;

            var ts = startTs.AddMilliseconds(i * intervalMs);
            yield return Make(deviceId, ts, type, value.Value, unit, correlationId);
        }
    }

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
}
