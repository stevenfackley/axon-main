namespace Axon.Core.Domain;

/// <summary>
/// Canonical set of biometric measurement types in the Axon Common Schema (ACS).
/// All vendor-specific metric names MUST be mapped to one of these values
/// during ingestion. Adding a new type requires a corresponding NormalizationMapper
/// unit-test suite before merging.
/// </summary>
public enum BiometricType : byte
{
    // ── Cardiovascular ────────────────────────────────────────────────────────
    HeartRate           = 0,
    HeartRateVariability = 1,
    RestingHeartRate    = 2,
    MaxHeartRate        = 3,
    CardiacOutput       = 4,

    // ── Respiratory ───────────────────────────────────────────────────────────
    RespiratoryRate     = 10,
    SpO2                = 11,

    // ── Sleep ─────────────────────────────────────────────────────────────────
    SleepDuration       = 20,
    SleepEfficiency     = 21,
    RemDuration         = 22,
    DeepSleepDuration   = 23,
    LightSleepDuration  = 24,
    SleepOnsetLatency   = 25,

    // ── Activity & Performance ────────────────────────────────────────────────
    Steps               = 30,
    ActiveEnergyBurned  = 31,
    BasalEnergyBurned   = 32,
    StrainScore         = 33,
    PowerOutput         = 34,
    Vo2Max              = 35,
    TrainingLoad        = 36,
    CadenceRunning      = 37,
    CadenceCycling      = 38,

    // ── Recovery & Readiness ──────────────────────────────────────────────────
    RecoveryScore       = 40,
    ReadinessScore      = 41,
    ResilienceScore     = 42,

    // ── Body Composition ──────────────────────────────────────────────────────
    BodyWeight          = 50,
    BodyFatPercentage   = 51,
    MuscleMass          = 52,
    BoneMass            = 53,
    Hydration           = 54,

    // ── Thermoregulation ──────────────────────────────────────────────────────
    SkinTemperature     = 60,
    CoreTemperature     = 61,

    // ── Metabolic / Blood ─────────────────────────────────────────────────────
    BloodGlucose        = 70,
    BloodPressureSystolic   = 71,
    BloodPressureDiastolic  = 72,
    CortisolLevel       = 73,

    // ── Environmental ─────────────────────────────────────────────────────────
    Altitude            = 80,
    AmbientTemperature  = 81,
    UvIndex             = 82,

    // ── Sentinel ──────────────────────────────────────────────────────────────
    Unknown             = 255
}
