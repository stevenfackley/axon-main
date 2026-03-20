using System.Text.Json.Serialization;

namespace Axon.Infrastructure.Drivers.Garmin;

// ──────────────────────────────────────────────────────────────────────────────
// Garmin Health API — intermediate record types
//
// These records mirror the Garmin Health API v1 JSON response shapes used by
// the Garmin Health API for consumer devices (Garmin Connect).
//
// API reference: https://developer.garmin.com/gc-developer-program/health-api/
//
// The Health API returns daily summary objects. Granular heart rate data is
// available via a separate intraday HR endpoint (sampled at 1-15 sec intervals).
//
// For file import, Garmin Connect exports data as JSON with the same field names
// used here, enabling the same NormalizationMapper to handle both paths.
// ──────────────────────────────────────────────────────────────────────────────

// ── Daily Summary ─────────────────────────────────────────────────────────────

/// <summary>
/// Garmin daily activity summary.
/// Covers steps, calories, HR, intensity minutes for a calendar day.
/// </summary>
public sealed record GarminDailySummary(
    [property: JsonPropertyName("userId")]                  string  UserId,
    [property: JsonPropertyName("userAccessToken")]         string  UserAccessToken,
    [property: JsonPropertyName("uploadStartTimeInSeconds")] long   UploadStartTimeInSeconds,
    [property: JsonPropertyName("uploadEndTimeInSeconds")]   long   UploadEndTimeInSeconds,
    [property: JsonPropertyName("summaryId")]               string  SummaryId,
    [property: JsonPropertyName("activityType")]            string  ActivityType,
    [property: JsonPropertyName("startTimeInSeconds")]      long    StartTimeInSeconds,
    [property: JsonPropertyName("startTimeOffsetInSeconds")] int    StartTimeOffsetInSeconds,
    [property: JsonPropertyName("durationInSeconds")]       long    DurationInSeconds,
    [property: JsonPropertyName("steps")]                   int?    Steps,
    [property: JsonPropertyName("distanceInMeters")]        float?  DistanceInMeters,
    [property: JsonPropertyName("activeTimeInSeconds")]     long?   ActiveTimeInSeconds,
    [property: JsonPropertyName("activeKilocalories")]      float?  ActiveKilocalories,
    [property: JsonPropertyName("bmrKilocalories")]         float?  BmrKilocalories,
    [property: JsonPropertyName("consumedCalories")]        float?  ConsumedCalories,
    [property: JsonPropertyName("averageHeartRateInBeatsPerMinute")]  int?  AverageHeartRateInBeatsPerMinute,
    [property: JsonPropertyName("maxHeartRateInBeatsPerMinute")]      int?  MaxHeartRateInBeatsPerMinute,
    [property: JsonPropertyName("restingHeartRateInBeatsPerMinute")]  int?  RestingHeartRateInBeatsPerMinute,
    [property: JsonPropertyName("minHeartRateInBeatsPerMinute")]      int?  MinHeartRateInBeatsPerMinute,
    [property: JsonPropertyName("averageStressLevel")]      int?    AverageStressLevel,
    [property: JsonPropertyName("maxStressLevel")]          int?    MaxStressLevel,
    [property: JsonPropertyName("stressQualifier")]         string? StressQualifier,
    [property: JsonPropertyName("floorsClimbed")]           int?    FloorsClimbed,
    [property: JsonPropertyName("averageSpO2Value")]        float?  AverageSpO2Value,
    [property: JsonPropertyName("onDemandSpO2Value")]       float?  OnDemandSpO2Value);

/// <summary>Push notification wrapper for daily summaries from Garmin Health API.</summary>
public sealed record GarminDailySummaryList(
    [property: JsonPropertyName("dailies")] IReadOnlyList<GarminDailySummary> Dailies);

// ── Sleep Summary ─────────────────────────────────────────────────────────────

/// <summary>Garmin sleep summary for one night.</summary>
public sealed record GarminSleepSummary(
    [property: JsonPropertyName("userId")]                  string  UserId,
    [property: JsonPropertyName("summaryId")]               string  SummaryId,
    [property: JsonPropertyName("calendarDate")]            string  CalendarDate,
    [property: JsonPropertyName("startTimeInSeconds")]      long    StartTimeInSeconds,
    [property: JsonPropertyName("startTimeOffsetInSeconds")] int    StartTimeOffsetInSeconds,
    [property: JsonPropertyName("durationInSeconds")]       long    DurationInSeconds,
    [property: JsonPropertyName("unmeasurableSleepInSeconds")] long? UnmeasurableSleepInSeconds,
    [property: JsonPropertyName("deepSleepDurationInSeconds")] long? DeepSleepDurationInSeconds,
    [property: JsonPropertyName("lightSleepDurationInSeconds")] long? LightSleepDurationInSeconds,
    [property: JsonPropertyName("remSleepInSeconds")]        long?  RemSleepInSeconds,
    [property: JsonPropertyName("awakeDurationInSeconds")]   long?  AwakeDurationInSeconds,
    [property: JsonPropertyName("sleepLevelsMap")]           GarminSleepLevelsMap? SleepLevelsMap,
    [property: JsonPropertyName("validation")]               string? Validation,
    [property: JsonPropertyName("timeOffsetSleepRespiration")] IReadOnlyDictionary<string, float>? TimeOffsetSleepRespiration,
    [property: JsonPropertyName("timeOffsetSleepSpo2")]      IReadOnlyDictionary<string, float>?  TimeOffsetSleepSpo2,
    [property: JsonPropertyName("averageSpO2Value")]         float?  AverageSpO2Value,
    [property: JsonPropertyName("lowestSpO2Value")]          float?  LowestSpO2Value,
    [property: JsonPropertyName("highestSpO2Value")]         float?  HighestSpO2Value,
    [property: JsonPropertyName("averageRespirationValue")]  float?  AverageRespirationValue,
    [property: JsonPropertyName("lowestRespirationValue")]   float?  LowestRespirationValue,
    [property: JsonPropertyName("highestRespirationValue")]  float?  HighestRespirationValue,
    [property: JsonPropertyName("averageStressLevel")]       int?    AverageStressLevel,
    [property: JsonPropertyName("sleepScores")]              GarminSleepScores? SleepScores);

/// <summary>Epoch-keyed maps of sleep stage intervals.</summary>
public sealed record GarminSleepLevelsMap(
    [property: JsonPropertyName("deep")]  IReadOnlyList<GarminSleepLevelEntry>? Deep,
    [property: JsonPropertyName("light")] IReadOnlyList<GarminSleepLevelEntry>? Light,
    [property: JsonPropertyName("rem")]   IReadOnlyList<GarminSleepLevelEntry>? Rem,
    [property: JsonPropertyName("awake")] IReadOnlyList<GarminSleepLevelEntry>? Awake);

/// <summary>A single timed sleep-stage interval.</summary>
public sealed record GarminSleepLevelEntry(
    [property: JsonPropertyName("startTimeInSeconds")] long  StartTimeInSeconds,
    [property: JsonPropertyName("endTimeInSeconds")]   long  EndTimeInSeconds);

/// <summary>Garmin sleep quality scores.</summary>
public sealed record GarminSleepScores(
    [property: JsonPropertyName("overall")]     GarminSleepScore? Overall,
    [property: JsonPropertyName("remSleep")]    GarminSleepScore? RemSleep,
    [property: JsonPropertyName("deepSleep")]   GarminSleepScore? DeepSleep,
    [property: JsonPropertyName("lightSleep")]  GarminSleepScore? LightSleep);

/// <summary>A qualifier + value pair for a Garmin sleep score dimension.</summary>
public sealed record GarminSleepScore(
    [property: JsonPropertyName("qualifierKey")] string Qualifier,
    [property: JsonPropertyName("value")]        int    Value);

/// <summary>Push notification wrapper for sleep summaries.</summary>
public sealed record GarminSleepSummaryList(
    [property: JsonPropertyName("sleeps")] IReadOnlyList<GarminSleepSummary> Sleeps);

// ── Heart Rate Variability ────────────────────────────────────────────────────

/// <summary>Garmin HRV summary (nightly average and 5-minute samples).</summary>
public sealed record GarminHrvSummary(
    [property: JsonPropertyName("userId")]            string  UserId,
    [property: JsonPropertyName("summaryId")]         string  SummaryId,
    [property: JsonPropertyName("calendarDate")]      string  CalendarDate,
    [property: JsonPropertyName("startTimeInSeconds")] long   StartTimeInSeconds,
    [property: JsonPropertyName("lastNight")]         GarminHrvLastNight? LastNight,
    [property: JsonPropertyName("hrvReadings")]       IReadOnlyList<GarminHrvReading>? HrvReadings);

/// <summary>Aggregate HRV metrics for the most recent sleep night.</summary>
public sealed record GarminHrvLastNight(
    [property: JsonPropertyName("weeklyAverage")]     int?  WeeklyAverage,
    [property: JsonPropertyName("lastNight")]         int?  LastNightAverage,
    [property: JsonPropertyName("lastFiveDaysAverage")] int? LastFiveDaysAverage,
    [property: JsonPropertyName("baseline")]          GarminHrvBaseline? Baseline);

/// <summary>HRV baseline thresholds for low/balanced/high classification.</summary>
public sealed record GarminHrvBaseline(
    [property: JsonPropertyName("lowUpper")]          int?  LowUpper,
    [property: JsonPropertyName("balancedLow")]       int?  BalancedLow,
    [property: JsonPropertyName("balancedUpper")]     int?  BalancedUpper,
    [property: JsonPropertyName("markerValue")]       int?  MarkerValue);

/// <summary>A single 5-minute HRV reading during sleep.</summary>
public sealed record GarminHrvReading(
    [property: JsonPropertyName("hrv")]               int   Hrv,
    [property: JsonPropertyName("startTimeInSeconds")] long StartTimeInSeconds);

/// <summary>Push notification wrapper for HRV summaries.</summary>
public sealed record GarminHrvSummaryList(
    [property: JsonPropertyName("hrvSummaries")] IReadOnlyList<GarminHrvSummary> HrvSummaries);

// ── Body Composition ──────────────────────────────────────────────────────────

/// <summary>Garmin body composition measurement from a smart scale sync.</summary>
public sealed record GarminBodyComposition(
    [property: JsonPropertyName("userId")]              string  UserId,
    [property: JsonPropertyName("summaryId")]           string  SummaryId,
    [property: JsonPropertyName("measurementTimeInSeconds")] long MeasurementTimeInSeconds,
    [property: JsonPropertyName("measurementTimeOffset")] int   MeasurementTimeOffset,
    [property: JsonPropertyName("muscleMassInGrams")]   int?    MuscleMassInGrams,
    [property: JsonPropertyName("boneMassInGrams")]     int?    BoneMassInGrams,
    [property: JsonPropertyName("bodyWaterInPercent")]  float?  BodyWaterInPercent,
    [property: JsonPropertyName("bodyFatInPercent")]    float?  BodyFatInPercent,
    [property: JsonPropertyName("bodyMassIndex")]       float?  BodyMassIndex,
    [property: JsonPropertyName("weightInGrams")]       int?    WeightInGrams);

/// <summary>Push notification wrapper for body composition measurements.</summary>
public sealed record GarminBodyCompositionList(
    [property: JsonPropertyName("compositions")] IReadOnlyList<GarminBodyComposition> Compositions);
