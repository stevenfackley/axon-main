using System.Text.Json.Serialization;

namespace Axon.Infrastructure.Drivers.Oura;

// ──────────────────────────────────────────────────────────────────────────────
// Oura Ring API v2 — intermediate record types
//
// These records mirror the Oura Ring API v2 JSON response shapes.
// API reference: https://cloud.ouraring.com/v2/docs
//
// Oura uses OAuth2 (authorization code flow) or Personal Access Tokens.
// The base URL is https://api.ouraring.com/v2/usercollection/.
//
// All timestamps use ISO-8601 format. Oura date fields use "YYYY-MM-DD".
// ──────────────────────────────────────────────────────────────────────────────

// ── Daily Readiness ───────────────────────────────────────────────────────────

/// <summary>Oura daily readiness score for one calendar day.</summary>
public sealed record OuraDailyReadiness(
    [property: JsonPropertyName("id")]                      string                      Id,
    [property: JsonPropertyName("day")]                     string                      Day,
    [property: JsonPropertyName("score")]                   int?                        Score,
    [property: JsonPropertyName("temperature_deviation")]   float?                      TemperatureDeviation,
    [property: JsonPropertyName("temperature_trend_deviation")] float?                  TemperatureTrendDeviation,
    [property: JsonPropertyName("timestamp")]               string                      Timestamp,
    [property: JsonPropertyName("contributors")]            OuraReadinessContributors?  Contributors);

/// <summary>Individual readiness contributor scores (0–100).</summary>
public sealed record OuraReadinessContributors(
    [property: JsonPropertyName("activity_balance")]        int?  ActivityBalance,
    [property: JsonPropertyName("body_temperature")]        int?  BodyTemperature,
    [property: JsonPropertyName("hrv_balance")]             int?  HrvBalance,
    [property: JsonPropertyName("previous_day_activity")]   int?  PreviousDayActivity,
    [property: JsonPropertyName("previous_night")]          int?  PreviousNight,
    [property: JsonPropertyName("recovery_index")]          int?  RecoveryIndex,
    [property: JsonPropertyName("resting_heart_rate")]      int?  RestingHeartRate,
    [property: JsonPropertyName("sleep_balance")]           int?  SleepBalance);

/// <summary>Paginated list response for <see cref="OuraDailyReadiness"/>.</summary>
public sealed record OuraDailyReadinessList(
    [property: JsonPropertyName("data")]          IReadOnlyList<OuraDailyReadiness> Data,
    [property: JsonPropertyName("next_token")]    string? NextToken);

// ── Daily Sleep ───────────────────────────────────────────────────────────────

/// <summary>Oura daily sleep aggregate for one calendar day (summary across all sleep sessions).</summary>
public sealed record OuraDailySleep(
    [property: JsonPropertyName("id")]                  string                  Id,
    [property: JsonPropertyName("day")]                 string                  Day,
    [property: JsonPropertyName("score")]               int?                    Score,
    [property: JsonPropertyName("timestamp")]           string                  Timestamp,
    [property: JsonPropertyName("contributors")]        OuraSleepContributors?  Contributors);

/// <summary>Individual sleep quality contributor scores (0–100).</summary>
public sealed record OuraSleepContributors(
    [property: JsonPropertyName("deep_sleep")]          int?  DeepSleep,
    [property: JsonPropertyName("efficiency")]          int?  Efficiency,
    [property: JsonPropertyName("latency")]             int?  Latency,
    [property: JsonPropertyName("rem_sleep")]           int?  RemSleep,
    [property: JsonPropertyName("restfulness")]         int?  Restfulness,
    [property: JsonPropertyName("timing")]              int?  Timing,
    [property: JsonPropertyName("total_sleep")]         int?  TotalSleep);

/// <summary>Paginated list response for <see cref="OuraDailySleep"/>.</summary>
public sealed record OuraDailySleepList(
    [property: JsonPropertyName("data")]          IReadOnlyList<OuraDailySleep> Data,
    [property: JsonPropertyName("next_token")]    string? NextToken);

// ── Sleep Session (granular) ──────────────────────────────────────────────────

/// <summary>
/// Oura detailed sleep session record — one record per sleep episode
/// (may be multiple per day for naps).
/// </summary>
public sealed record OuraSleepSession(
    [property: JsonPropertyName("id")]                          string  Id,
    [property: JsonPropertyName("average_breath")]              float?  AverageBreath,
    [property: JsonPropertyName("average_heart_rate")]          float?  AverageHeartRate,
    [property: JsonPropertyName("average_hrv")]                 int?    AverageHrv,
    [property: JsonPropertyName("awake_time")]                  int?    AwakeTime,
    [property: JsonPropertyName("bedtime_end")]                 string  BedtimeEnd,
    [property: JsonPropertyName("bedtime_start")]               string  BedtimeStart,
    [property: JsonPropertyName("day")]                         string  Day,
    [property: JsonPropertyName("deep_sleep_duration")]         int?    DeepSleepDuration,
    [property: JsonPropertyName("efficiency")]                  int?    Efficiency,
    [property: JsonPropertyName("heart_rate")]                  OuraTimeSeries?   HeartRate,
    [property: JsonPropertyName("hrv")]                         OuraTimeSeries?   Hrv,
    [property: JsonPropertyName("latency")]                     int?    Latency,
    [property: JsonPropertyName("light_sleep_duration")]        int?    LightSleepDuration,
    [property: JsonPropertyName("low_battery_alert")]           bool    LowBatteryAlert,
    [property: JsonPropertyName("lowest_heart_rate")]           int?    LowestHeartRate,
    [property: JsonPropertyName("movement_30_sec")]             string? Movement30Sec,
    [property: JsonPropertyName("period")]                      int     Period,
    [property: JsonPropertyName("readiness")]                   OuraSleepReadiness? Readiness,
    [property: JsonPropertyName("readiness_score_delta")]       int?    ReadinessScoreDelta,
    [property: JsonPropertyName("rem_sleep_duration")]          int?    RemSleepDuration,
    [property: JsonPropertyName("restless_periods")]            int?    RestlessPeriods,
    [property: JsonPropertyName("sleep_phase_5_min")]           string? SleepPhase5Min,
    [property: JsonPropertyName("sleep_score_delta")]           int?    SleepScoreDelta,
    [property: JsonPropertyName("sleep_algorithm_version")]     string? SleepAlgorithmVersion,
    [property: JsonPropertyName("time_in_bed")]                 int?    TimeInBed,
    [property: JsonPropertyName("total_sleep_duration")]        int?    TotalSleepDuration,
    [property: JsonPropertyName("type")]                        string  Type);

/// <summary>A time-series of values with fixed interval spacing.</summary>
public sealed record OuraTimeSeries(
    [property: JsonPropertyName("interval")]    float               Interval,
    [property: JsonPropertyName("items")]       IReadOnlyList<float?> Items,
    [property: JsonPropertyName("timestamp")]   string              Timestamp);

/// <summary>Readiness scores embedded in a sleep session.</summary>
public sealed record OuraSleepReadiness(
    [property: JsonPropertyName("contributor")] OuraReadinessContributors? Contributor,
    [property: JsonPropertyName("score")]       int?  Score,
    [property: JsonPropertyName("temperature_deviation")] float? TemperatureDeviation,
    [property: JsonPropertyName("temperature_trend_deviation")] float? TemperatureTrendDeviation);

/// <summary>Paginated list response for <see cref="OuraSleepSession"/>.</summary>
public sealed record OuraSleepSessionList(
    [property: JsonPropertyName("data")]          IReadOnlyList<OuraSleepSession> Data,
    [property: JsonPropertyName("next_token")]    string? NextToken);

// ── Daily Activity ────────────────────────────────────────────────────────────

/// <summary>Oura daily activity summary.</summary>
public sealed record OuraDailyActivity(
    [property: JsonPropertyName("id")]                      string  Id,
    [property: JsonPropertyName("class_5_min")]             string? Class5Min,
    [property: JsonPropertyName("score")]                   int?    Score,
    [property: JsonPropertyName("active_calories")]         int?    ActiveCalories,
    [property: JsonPropertyName("average_met_minutes")]     float?  AverageMetMinutes,
    [property: JsonPropertyName("contributors")]            OuraActivityContributors? Contributors,
    [property: JsonPropertyName("equivalent_walking_distance")] int? EquivalentWalkingDistance,
    [property: JsonPropertyName("high_activity_met_minutes")] int?  HighActivityMetMinutes,
    [property: JsonPropertyName("high_activity_time")]      int?    HighActivityTime,
    [property: JsonPropertyName("inactivity_alerts")]       int?    InactivityAlerts,
    [property: JsonPropertyName("low_activity_met_minutes")] int?   LowActivityMetMinutes,
    [property: JsonPropertyName("low_activity_time")]       int?    LowActivityTime,
    [property: JsonPropertyName("medium_activity_met_minutes")] int? MediumActivityMetMinutes,
    [property: JsonPropertyName("medium_activity_time")]    int?    MediumActivityTime,
    [property: JsonPropertyName("met")]                     OuraTimeSeries? Met,
    [property: JsonPropertyName("meters_to_target")]        int?    MetersToTarget,
    [property: JsonPropertyName("non_wear_time")]           int?    NonWearTime,
    [property: JsonPropertyName("resting_time")]            int?    RestingTime,
    [property: JsonPropertyName("sedentary_met_minutes")]   int?    SedentaryMetMinutes,
    [property: JsonPropertyName("sedentary_time")]          int?    SedentaryTime,
    [property: JsonPropertyName("steps")]                   int?    Steps,
    [property: JsonPropertyName("target_calories")]         int?    TargetCalories,
    [property: JsonPropertyName("target_meters")]           int?    TargetMeters,
    [property: JsonPropertyName("total_calories")]          int?    TotalCalories,
    [property: JsonPropertyName("day")]                     string  Day,
    [property: JsonPropertyName("timestamp")]               string  Timestamp);

/// <summary>Individual activity contributor scores (0–100).</summary>
public sealed record OuraActivityContributors(
    [property: JsonPropertyName("meet_daily_targets")]      int?  MeetDailyTargets,
    [property: JsonPropertyName("move_every_hour")]         int?  MoveEveryHour,
    [property: JsonPropertyName("recovery_time")]           int?  RecoveryTime,
    [property: JsonPropertyName("stay_active")]             int?  StayActive,
    [property: JsonPropertyName("training_frequency")]      int?  TrainingFrequency,
    [property: JsonPropertyName("training_volume")]         int?  TrainingVolume);

/// <summary>Paginated list response for <see cref="OuraDailyActivity"/>.</summary>
public sealed record OuraDailyActivityList(
    [property: JsonPropertyName("data")]          IReadOnlyList<OuraDailyActivity> Data,
    [property: JsonPropertyName("next_token")]    string? NextToken);

// ── Heart Rate ────────────────────────────────────────────────────────────────

/// <summary>A single heart-rate measurement from continuous HR tracking.</summary>
public sealed record OuraHeartRateSample(
    [property: JsonPropertyName("bpm")]         int     Bpm,
    [property: JsonPropertyName("source")]      string  Source,
    [property: JsonPropertyName("timestamp")]   string  Timestamp);

/// <summary>Paginated list response for <see cref="OuraHeartRateSample"/>.</summary>
public sealed record OuraHeartRateList(
    [property: JsonPropertyName("data")]          IReadOnlyList<OuraHeartRateSample> Data,
    [property: JsonPropertyName("next_token")]    string? NextToken);

// ── SpO2 ──────────────────────────────────────────────────────────────────────

/// <summary>Continuous SpO2 measurement from the Oura Ring.</summary>
public sealed record OuraSpO2Daily(
    [property: JsonPropertyName("id")]              string      Id,
    [property: JsonPropertyName("day")]             string      Day,
    [property: JsonPropertyName("spo2_percentage")] OuraSpo2Percentage? Spo2Percentage);

/// <summary>Aggregate SpO2 statistics for a day.</summary>
public sealed record OuraSpo2Percentage(
    [property: JsonPropertyName("average")]     float?  Average,
    [property: JsonPropertyName("min")]         float?  Min,
    [property: JsonPropertyName("max")]         float?  Max);

/// <summary>Paginated list response for <see cref="OuraSpO2Daily"/>.</summary>
public sealed record OuraSpO2List(
    [property: JsonPropertyName("data")]          IReadOnlyList<OuraSpO2Daily> Data,
    [property: JsonPropertyName("next_token")]    string? NextToken);
