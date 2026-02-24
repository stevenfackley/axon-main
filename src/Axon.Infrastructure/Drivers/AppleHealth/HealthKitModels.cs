// Apple HealthKit local data models – only compiled when targeting net9.0-ios.
#if IOS
namespace Axon.Infrastructure.Drivers.AppleHealth;

/// <summary>
/// Intermediate, vendor-neutral model for a HealthKit quantity sample
/// (heart rate, HRV, steps, etc.).
///
/// This exists so <see cref="HealthKitNormalizationMapper"/> can operate on a
/// pure C# struct without a compile-time dependency on <c>HealthKit.HKSample</c>,
/// making the mapper independently unit-testable on non-iOS platforms.
/// </summary>
/// <param name="TypeIdentifier">
///     HealthKit type identifier string, e.g.
///     <c>HKQuantityTypeIdentifierHeartRate</c>.
/// </param>
/// <param name="StartDate">UTC start of the measurement window.</param>
/// <param name="EndDate">UTC end of the measurement window (equals StartDate for instantaneous readings).</param>
/// <param name="Value">Numeric value in the HK canonical unit for this type.</param>
/// <param name="HkUnit">
///     HealthKit unit string used when querying the value, e.g. "count/min", "ms", "%".
///     Required so the mapper can validate that the correct unit was requested.
/// </param>
/// <param name="DeviceId">Hardware identifier from <c>HKDevice</c>, or <c>"Apple"</c> fallback.</param>
/// <param name="FirmwareVersion">Optional firmware string from <c>HKDevice.FirmwareVersion</c>.</param>
public readonly record struct HKQuantitySampleResult(
    string         TypeIdentifier,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    double         Value,
    string         HkUnit,
    string         DeviceId,
    string?        FirmwareVersion);

/// <summary>
/// Intermediate model for a HealthKit sleep category sample
/// (<c>HKCategoryTypeIdentifierSleepAnalysis</c>).
///
/// HealthKit stores individual sleep stage intervals as separate category samples
/// with a <see cref="SleepStage"/> discriminator. Axon aggregates these into
/// per-stage duration events before writing to the ACS schema.
/// </summary>
/// <param name="Stage">Apple Health sleep stage enum value (maps to <see cref="HKSleepStage"/>).</param>
/// <param name="StartDate">UTC start of the sleep stage interval.</param>
/// <param name="EndDate">UTC end of the sleep stage interval.</param>
/// <param name="DeviceId">Hardware identifier from <c>HKDevice</c>, or <c>"Apple"</c> fallback.</param>
public readonly record struct HKSleepSampleResult(
    HKSleepStage   Stage,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string         DeviceId);

/// <summary>
/// Maps <c>HKCategoryValueSleepAnalysis</c> integer values to a readable enum.
/// Mirrors the values defined in the HealthKit framework.
/// </summary>
public enum HKSleepStage : int
{
    InBed      = 0,
    Asleep     = 1,  // Unspecified sleep (legacy / Apple Watch pre-watchOS 9)
    Awake      = 2,
    AsleepCore = 3,  // Light sleep (watchOS 9+)
    AsleepDeep = 4,  // Deep / slow-wave sleep (watchOS 9+)
    AsleepREM  = 5,  // REM sleep (watchOS 9+)
}

/// <summary>
/// Intermediate model for a HealthKit blood pressure correlation sample
/// (<c>HKCorrelationTypeIdentifierBloodPressure</c>), which bundles systolic
/// and diastolic samples together.
/// </summary>
/// <param name="Systolic">Systolic pressure in mmHg.</param>
/// <param name="Diastolic">Diastolic pressure in mmHg.</param>
/// <param name="Timestamp">UTC measurement time.</param>
/// <param name="DeviceId">Hardware identifier.</param>
public readonly record struct HKBloodPressureResult(
    double         Systolic,
    double         Diastolic,
    DateTimeOffset Timestamp,
    string         DeviceId);
#endif
