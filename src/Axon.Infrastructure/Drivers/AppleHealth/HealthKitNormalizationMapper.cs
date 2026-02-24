#if IOS
using Axon.Core.Domain;

namespace Axon.Infrastructure.Drivers.AppleHealth;

/// <summary>
/// Maps Apple HealthKit sample data to the Axon Common Schema (ACS).
///
/// Unit-conversion reference
/// ──────────────────────────
/// All ACS units are SI or widely-accepted health convention:
///
///   HealthKit Type                          HK Unit     ACS Value      ACS Unit
///   ─────────────────────────────────────── ─────────── ────────────── ─────────
///   HKQuantityTypeIdentifierHeartRate       count/min   as-is          bpm
///   HKQuantityTypeIdentifierHRVSDNN         ms          as-is          ms
///   HKQuantityTypeIdentifierRestingHR       count/min   as-is          bpm
///   HKQuantityTypeIdentifierRespiratoryRate count/min   as-is          breaths/min
///   HKQuantityTypeIdentifierOxygenSat       %           value × 100    %
///   HKQuantityTypeIdentifierStepCount       count       as-is          steps
///   HKQuantityTypeIdentifierActiveEnergy    kcal        as-is          kcal
///   HKQuantityTypeIdentifierBasalEnergy     kcal        as-is          kcal
///   HKQuantityTypeIdentifierVO2Max          mL/kg/min   as-is          mL/kg/min
///   HKQuantityTypeIdentifierCyclingPower    W           as-is          W
///   HKQuantityTypeIdentifierBodyMass        kg          as-is          kg
///   HKQuantityTypeIdentifierBodyFat         %           value × 100    %
///   HKQuantityTypeIdentifierLeanBodyMass    kg          as-is          kg
///   HKQuantityTypeIdentifierBodyTemp        degC        as-is          °C
///   HKQuantityTypeIdentifierBloodGlucose    mmol/L      as-is          mmol/L
///   HKQuantityTypeIdentifierBPSystolic      mmHg        as-is          mmHg
///   HKQuantityTypeIdentifierBPDiastolic     mmHg        as-is          mmHg
///
/// HealthKit returns SpO2 and body-fat as fractions [0.0, 1.0].
/// The mapper multiplies by 100 to yield a percentage [0, 100] per ACS convention.
///
/// All mapper methods are static and pure — no side effects, no I/O.
/// 100% unit-test coverage is required (DoD) before any merge.
/// </summary>
public static class HealthKitNormalizationMapper
{
    private const string Vendor = "Apple";

    // ── Quantity Samples ───────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="HKQuantitySampleResult"/> to a <see cref="BiometricEvent"/>.
    /// Returns <c>null</c> for unrecognised <paramref name="sample"/> type identifiers
    /// so callers can choose to skip or log unknown types without throwing.
    /// </summary>
    public static BiometricEvent? MapQuantitySample(
        HKQuantitySampleResult sample,
        string? correlationId = null)
    {
        var (type, unit, value) = sample.TypeIdentifier switch
        {
            // ── Cardiovascular ───────────────────────────────────────────────
            "HKQuantityTypeIdentifierHeartRate" =>
                (BiometricType.HeartRate, "bpm", sample.Value),

            "HKQuantityTypeIdentifierHeartRateVariabilitySDNN" =>
                (BiometricType.HeartRateVariability, "ms", sample.Value),

            "HKQuantityTypeIdentifierRestingHeartRate" =>
                (BiometricType.RestingHeartRate, "bpm", sample.Value),

            // ── Respiratory ──────────────────────────────────────────────────
            "HKQuantityTypeIdentifierRespiratoryRate" =>
                (BiometricType.RespiratoryRate, "breaths/min", sample.Value),

            // HK returns SpO2 as a fraction [0,1]; ACS expects percentage [0,100].
            "HKQuantityTypeIdentifierOxygenSaturation" =>
                (BiometricType.SpO2, "%", sample.Value * 100.0),

            // ── Activity & Performance ───────────────────────────────────────
            "HKQuantityTypeIdentifierStepCount" =>
                (BiometricType.Steps, "steps", sample.Value),

            "HKQuantityTypeIdentifierActiveEnergyBurned" =>
                (BiometricType.ActiveEnergyBurned, "kcal", sample.Value),

            "HKQuantityTypeIdentifierBasalEnergyBurned" =>
                (BiometricType.BasalEnergyBurned, "kcal", sample.Value),

            "HKQuantityTypeIdentifierVO2Max" =>
                (BiometricType.Vo2Max, "mL/kg/min", sample.Value),

            "HKQuantityTypeIdentifierCyclingPower" =>
                (BiometricType.PowerOutput, "W", sample.Value),

            // ── Body Composition ─────────────────────────────────────────────
            "HKQuantityTypeIdentifierBodyMass" =>
                (BiometricType.BodyWeight, "kg", sample.Value),

            // HK returns body fat as a fraction [0,1]; ACS expects percentage.
            "HKQuantityTypeIdentifierBodyFatPercentage" =>
                (BiometricType.BodyFatPercentage, "%", sample.Value * 100.0),

            "HKQuantityTypeIdentifierLeanBodyMass" =>
                (BiometricType.MuscleMass, "kg", sample.Value),

            // ── Thermoregulation ─────────────────────────────────────────────
            "HKQuantityTypeIdentifierBodyTemperature" =>
                (BiometricType.CoreTemperature, "°C", sample.Value),

            // ── Metabolic / Blood ────────────────────────────────────────────
            "HKQuantityTypeIdentifierBloodGlucose" =>
                (BiometricType.BloodGlucose, "mmol/L", sample.Value),

            // Blood pressure is handled by MapBloodPressure; if individual
            // samples appear here, map them directly.
            "HKQuantityTypeIdentifierBloodPressureSystolic" =>
                (BiometricType.BloodPressureSystolic, "mmHg", sample.Value),

            "HKQuantityTypeIdentifierBloodPressureDiastolic" =>
                (BiometricType.BloodPressureDiastolic, "mmHg", sample.Value),

            // Unknown / unmapped type — return null so the caller can skip.
            _ => ((BiometricType?)null, (string?)null, 0.0)
        };

        if (type is null || unit is null)
            return null;

        return new BiometricEvent(
            Id:            DeterministicId(sample.DeviceId, sample.StartDate, type.Value),
            Timestamp:     sample.StartDate,
            Type:          type.Value,
            Value:         value,
            Unit:          unit,
            Source:        BuildSource(sample.DeviceId, sample.FirmwareVersion),
            CorrelationId: correlationId);
    }

    // ── Sleep Samples ──────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a single <see cref="HKSleepSampleResult"/> interval to a
    /// <see cref="BiometricEvent"/> with a duration value (in seconds).
    ///
    /// Apple Health stores sleep as individual stage intervals.
    /// Axon records each interval's duration and stage type separately;
    /// aggregation to total nightly duration happens in the Analytics layer.
    /// </summary>
    public static BiometricEvent? MapSleepSample(
        HKSleepSampleResult sample,
        string? correlationId = null)
    {
        var type = sample.Stage switch
        {
            HKSleepStage.AsleepREM  => BiometricType.RemDuration,
            HKSleepStage.AsleepDeep => BiometricType.DeepSleepDuration,
            HKSleepStage.AsleepCore => BiometricType.LightSleepDuration,
            HKSleepStage.Asleep     => BiometricType.SleepDuration,    // legacy
            HKSleepStage.Awake      => null,   // in-bed awakenings — skip
            HKSleepStage.InBed      => null,   // raw in-bed time — skip
            _                       => null
        };

        if (type is null) return null;

        var durationSeconds = (sample.EndDate - sample.StartDate).TotalSeconds;

        return new BiometricEvent(
            Id:            DeterministicId(sample.DeviceId, sample.StartDate, type.Value),
            Timestamp:     sample.StartDate,
            Type:          type.Value,
            Value:         durationSeconds,
            Unit:          "s",
            Source:        BuildSource(sample.DeviceId, firmwareVersion: null),
            CorrelationId: correlationId);
    }

    // ── Blood Pressure Correlation ─────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="HKBloodPressureResult"/> correlation (which bundles
    /// systolic + diastolic) into two separate <see cref="BiometricEvent"/> records.
    /// </summary>
    public static (BiometricEvent Systolic, BiometricEvent Diastolic) MapBloodPressure(
        HKBloodPressureResult sample,
        string? correlationId = null)
    {
        var source = BuildSource(sample.DeviceId, firmwareVersion: null);

        var systolic = new BiometricEvent(
            Id:            DeterministicId(sample.DeviceId, sample.Timestamp, BiometricType.BloodPressureSystolic),
            Timestamp:     sample.Timestamp,
            Type:          BiometricType.BloodPressureSystolic,
            Value:         sample.Systolic,
            Unit:          "mmHg",
            Source:        source,
            CorrelationId: correlationId);

        var diastolic = new BiometricEvent(
            Id:            DeterministicId(sample.DeviceId, sample.Timestamp, BiometricType.BloodPressureDiastolic),
            Timestamp:     sample.Timestamp,
            Type:          BiometricType.BloodPressureDiastolic,
            Value:         sample.Diastolic,
            Unit:          "mmHg",
            Source:        source,
            CorrelationId: correlationId);

        return (systolic, diastolic);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a deterministic <see cref="Guid"/> from (deviceId, timestamp, type)
    /// so re-ingesting the same HealthKit sample always produces the same <c>Id</c>,
    /// enabling idempotent upserts in <see cref="Axon.Infrastructure.Persistence.BiometricRepository"/>.
    /// Uses UUID v5 (SHA-1 namespace) for stability without heap string allocation.
    /// </summary>
    private static Guid DeterministicId(
        string deviceId,
        DateTimeOffset timestamp,
        BiometricType type)
    {
        // UUID v5 namespace for Axon Apple Health events
        var ns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"); // URL namespace
        var input = $"apple|{deviceId}|{timestamp:O}|{(byte)type}";
        return GuidV5.Create(ns, input);
    }

    private static SourceMetadata BuildSource(string deviceId, string? firmwareVersion) =>
        new(
            DeviceId:           deviceId,
            Vendor:             Vendor,
            FirmwareVersion:    firmwareVersion,
            ConfidenceScore:    0.95f,   // Apple Health is considered high-confidence
            IngestionTimestamp: DateTimeOffset.UtcNow);
}

/// <summary>
/// Minimal UUID v5 (SHA-1 hash) generator for deterministic ID derivation.
/// Avoids pulling in a NuGet dependency for a single utility.
/// </summary>
file static class GuidV5
{
    public static Guid Create(Guid namespaceId, string name)
    {
        var nsBytes = namespaceId.ToByteArray();
        // Convert namespace GUID bytes from little-endian to big-endian (RFC 4122)
        SwapBytes(nsBytes, 0, 3);
        SwapBytes(nsBytes, 1, 2);
        SwapBytes(nsBytes, 4, 5);
        SwapBytes(nsBytes, 6, 7);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var combined  = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes,  0, combined, 0,            nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, combined, nsBytes.Length, nameBytes.Length);

        Span<byte> hash = stackalloc byte[20];
        System.Security.Cryptography.SHA1.HashData(combined, hash);

        // Set version 5 and variant bits per RFC 4122 §4.3
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var guidBytes = hash[..16].ToArray();
        // Convert back to little-endian for .NET Guid constructor
        SwapBytes(guidBytes, 0, 3);
        SwapBytes(guidBytes, 1, 2);
        SwapBytes(guidBytes, 4, 5);
        SwapBytes(guidBytes, 6, 7);

        return new Guid(guidBytes);
    }

    private static void SwapBytes(byte[] b, int i, int j)
        => (b[i], b[j]) = (b[j], b[i]);
}
#endif
