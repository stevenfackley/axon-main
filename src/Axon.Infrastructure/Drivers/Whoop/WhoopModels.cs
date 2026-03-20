using System.Text.Json.Serialization;

namespace Axon.Infrastructure.Drivers.Whoop;

// ──────────────────────────────────────────────────────────────────────────────
// Whoop API v1 — intermediate record types
//
// These records mirror the Whoop REST API v1 JSON response shapes and decouple
// the NormalizationMapper from raw JSON tokens, making the mapper unit-testable
// on any platform without a live Whoop connection.
//
// API reference: https://developer.whoop.com/api
// All timestamps are ISO-8601 UTC strings in the Whoop API.
// ──────────────────────────────────────────────────────────────────────────────

// ── Recovery ──────────────────────────────────────────────────────────────────

/// <summary>A single Whoop recovery record for one physiological cycle.</summary>
public sealed record WhoopRecovery(
    [property: JsonPropertyName("id")]              long             Id,
    [property: JsonPropertyName("cycle_id")]        long             CycleId,
    [property: JsonPropertyName("sleep_id")]        long             SleepId,
    [property: JsonPropertyName("user_id")]         long             UserId,
    [property: JsonPropertyName("created_at")]      string           CreatedAt,
    [property: JsonPropertyName("updated_at")]      string           UpdatedAt,
    [property: JsonPropertyName("score_state")]     string           ScoreState,
    [property: JsonPropertyName("score")]           WhoopRecoveryScore? Score);

/// <summary>Scored biometric values inside a <see cref="WhoopRecovery"/>.</summary>
public sealed record WhoopRecoveryScore(
    [property: JsonPropertyName("user_calibrating")]    bool    UserCalibrating,
    [property: JsonPropertyName("recovery_score")]      float   RecoveryScore,
    [property: JsonPropertyName("resting_heart_rate")]  float   RestingHeartRate,
    [property: JsonPropertyName("hrv_rmssd_milli")]     float   HrvRmssdMilli,
    [property: JsonPropertyName("spo2_percentage")]     float?  Spo2Percentage,
    [property: JsonPropertyName("skin_temp_celsius")]   float?  SkinTempCelsius);

/// <summary>Paginated list response for <see cref="WhoopRecovery"/> records.</summary>
public sealed record WhoopRecoveryList(
    [property: JsonPropertyName("records")]     IReadOnlyList<WhoopRecovery> Records,
    [property: JsonPropertyName("next_token")] string? NextToken);

// ── Sleep ─────────────────────────────────────────────────────────────────────

/// <summary>A single Whoop sleep record.</summary>
public sealed record WhoopSleep(
    [property: JsonPropertyName("id")]              long              Id,
    [property: JsonPropertyName("user_id")]         long              UserId,
    [property: JsonPropertyName("created_at")]      string            CreatedAt,
    [property: JsonPropertyName("updated_at")]      string            UpdatedAt,
    [property: JsonPropertyName("start")]           string            Start,
    [property: JsonPropertyName("end")]             string            End,
    [property: JsonPropertyName("nap")]             bool              IsNap,
    [property: JsonPropertyName("score_state")]     string            ScoreState,
    [property: JsonPropertyName("score")]           WhoopSleepScore?  Score);

/// <summary>Scored values inside a <see cref="WhoopSleep"/>.</summary>
public sealed record WhoopSleepScore(
    [property: JsonPropertyName("stage_summary")]          WhoopSleepStageSummary? StageSummary,
    [property: JsonPropertyName("sleep_needed")]           WhoopSleepNeeded?       SleepNeeded,
    [property: JsonPropertyName("respiratory_rate")]       float?  RespiratoryRate,
    [property: JsonPropertyName("sleep_performance_percentage")] float? SleepPerformancePercentage,
    [property: JsonPropertyName("sleep_consistency_percentage")] float? SleepConsistencyPercentage,
    [property: JsonPropertyName("sleep_efficiency_percentage")]  float? SleepEfficiencyPercentage);

/// <summary>Sleep stage breakdown in milliseconds.</summary>
public sealed record WhoopSleepStageSummary(
    [property: JsonPropertyName("total_in_bed_time_milli")]         long TotalInBedTimeMilli,
    [property: JsonPropertyName("total_awake_time_milli")]          long TotalAwakeTimeMilli,
    [property: JsonPropertyName("total_no_data_time_milli")]        long TotalNoDataTimeMilli,
    [property: JsonPropertyName("total_light_sleep_time_milli")]    long TotalLightSleepTimeMilli,
    [property: JsonPropertyName("total_slow_wave_sleep_time_milli")] long TotalSlowWaveSleepTimeMilli,
    [property: JsonPropertyName("total_rem_sleep_time_milli")]      long TotalRemSleepTimeMilli,
    [property: JsonPropertyName("sleep_cycle_count")]               int  SleepCycleCount,
    [property: JsonPropertyName("disturbance_count")]               int  DisturbanceCount);

/// <summary>Recommended sleep debt tracking.</summary>
public sealed record WhoopSleepNeeded(
    [property: JsonPropertyName("baseline_milli")]          long BaselineMilli,
    [property: JsonPropertyName("need_from_sleep_debt_milli")] long NeedFromSleepDebtMilli,
    [property: JsonPropertyName("need_from_recent_strain_milli")] long NeedFromRecentStrainMilli,
    [property: JsonPropertyName("need_from_recent_nap_milli")] long NeedFromRecentNapMilli);

/// <summary>Paginated list response for <see cref="WhoopSleep"/> records.</summary>
public sealed record WhoopSleepList(
    [property: JsonPropertyName("records")]     IReadOnlyList<WhoopSleep> Records,
    [property: JsonPropertyName("next_token")] string? NextToken);

// ── Cycle (Strain) ────────────────────────────────────────────────────────────

/// <summary>A single Whoop physiological cycle (daily strain period).</summary>
public sealed record WhoopCycle(
    [property: JsonPropertyName("id")]              long              Id,
    [property: JsonPropertyName("user_id")]         long              UserId,
    [property: JsonPropertyName("created_at")]      string            CreatedAt,
    [property: JsonPropertyName("updated_at")]      string            UpdatedAt,
    [property: JsonPropertyName("start")]           string            Start,
    [property: JsonPropertyName("end")]             string?           End,
    [property: JsonPropertyName("score_state")]     string            ScoreState,
    [property: JsonPropertyName("score")]           WhoopCycleScore?  Score);

/// <summary>Scored values inside a <see cref="WhoopCycle"/>.</summary>
public sealed record WhoopCycleScore(
    [property: JsonPropertyName("strain")]              float  Strain,
    [property: JsonPropertyName("kilojoule")]           float  Kilojoule,
    [property: JsonPropertyName("average_heart_rate")]  int    AverageHeartRate,
    [property: JsonPropertyName("max_heart_rate")]      int    MaxHeartRate);

/// <summary>Paginated list response for <see cref="WhoopCycle"/> records.</summary>
public sealed record WhoopCycleList(
    [property: JsonPropertyName("records")]     IReadOnlyList<WhoopCycle> Records,
    [property: JsonPropertyName("next_token")] string? NextToken);

// ── Workout ───────────────────────────────────────────────────────────────────

/// <summary>A single Whoop workout activity.</summary>
public sealed record WhoopWorkout(
    [property: JsonPropertyName("id")]              long              Id,
    [property: JsonPropertyName("user_id")]         long              UserId,
    [property: JsonPropertyName("created_at")]      string            CreatedAt,
    [property: JsonPropertyName("updated_at")]      string            UpdatedAt,
    [property: JsonPropertyName("start")]           string            Start,
    [property: JsonPropertyName("end")]             string            End,
    [property: JsonPropertyName("sport_id")]        int               SportId,
    [property: JsonPropertyName("score_state")]     string            ScoreState,
    [property: JsonPropertyName("score")]           WhoopWorkoutScore? Score);

/// <summary>Scored values inside a <see cref="WhoopWorkout"/>.</summary>
public sealed record WhoopWorkoutScore(
    [property: JsonPropertyName("strain")]              float   Strain,
    [property: JsonPropertyName("average_heart_rate")]  int     AverageHeartRate,
    [property: JsonPropertyName("max_heart_rate")]      int     MaxHeartRate,
    [property: JsonPropertyName("kilojoule")]           float   Kilojoule,
    [property: JsonPropertyName("percent_recorded")]    float   PercentRecorded,
    [property: JsonPropertyName("distance_meter")]      float?  DistanceMeter,
    [property: JsonPropertyName("altitude_gain_meter")] float?  AltitudeGainMeter,
    [property: JsonPropertyName("altitude_change_meter")] float? AltitudeChangeMeter,
    [property: JsonPropertyName("zone_duration")]       WhoopZoneDuration? ZoneDuration);

/// <summary>Heart-rate zone durations in milliseconds.</summary>
public sealed record WhoopZoneDuration(
    [property: JsonPropertyName("zone_zero_milli")]  long ZoneZeroMilli,
    [property: JsonPropertyName("zone_one_milli")]   long ZoneOneMilli,
    [property: JsonPropertyName("zone_two_milli")]   long ZoneTwoMilli,
    [property: JsonPropertyName("zone_three_milli")] long ZoneThreeMilli,
    [property: JsonPropertyName("zone_four_milli")]  long ZoneFourMilli,
    [property: JsonPropertyName("zone_five_milli")]  long ZoneFiveMilli);

/// <summary>Paginated list response for <see cref="WhoopWorkout"/> records.</summary>
public sealed record WhoopWorkoutList(
    [property: JsonPropertyName("records")]     IReadOnlyList<WhoopWorkout> Records,
    [property: JsonPropertyName("next_token")] string? NextToken);

// ── Body Measurement ──────────────────────────────────────────────────────────

/// <summary>User body measurement snapshot from the Whoop profile.</summary>
public sealed record WhoopBodyMeasurement(
    [property: JsonPropertyName("height_meter")]        float HeightMeter,
    [property: JsonPropertyName("weight_kilogram")]     float WeightKilogram,
    [property: JsonPropertyName("max_heart_rate")]      int   MaxHeartRate);
