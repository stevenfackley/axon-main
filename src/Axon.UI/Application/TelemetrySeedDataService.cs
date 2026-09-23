using System.Buffers.Binary;
using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.UI.Application;

/// <summary>
/// Generates seed data for local testing and development: a realistic 30-day
/// time series across the dashboard's biometric types.
/// Opt-in only (see <see cref="IsEnabled"/>): seeding writes through the normal
/// ingest path, so every seeded row also lands in the sync outbox. It must never
/// run against a real user's database by default.
/// Idempotent: skips when the repository already holds any biometric data.
/// </summary>
internal sealed class TelemetrySeedDataService
{
    /// <summary>Environment variable that opts a run into demo-data seeding ("1" or "true").</summary>
    internal const string EnableVariable = "AXON_SEED_DEMO_DATA";

    internal const string SeedDeviceId = "local-dev-seed";
    internal const string SeedVendor = "Axon Seed Data";

    private const int SeedDays = 30;
    private const int RandomSeed = 42;

    private static readonly Dictionary<BiometricType, string> BiometricUnits = new()
    {
        { BiometricType.HeartRate, "bpm" },
        { BiometricType.HeartRateVariability, "ms" },
        { BiometricType.SpO2, "%" },
        { BiometricType.RecoveryScore, "score" },
        { BiometricType.ReadinessScore, "score" },
        { BiometricType.StrainScore, "score" },
        { BiometricType.SleepDuration, "hours" },
        { BiometricType.SleepEfficiency, "%" }
    };

    private readonly IBiometricRepository _repository;
    private readonly TimeProvider _timeProvider;

    public TelemetrySeedDataService(IBiometricRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True when <see cref="EnableVariable"/> is set to "1" or "true" (case-insensitive).</summary>
    public static bool IsEnabled(string? value) =>
        value is not null
        && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Seeds the repository when it holds no biometric data of any type.
    /// Returns true when seed data was written.
    /// </summary>
    public async ValueTask<bool> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        var latest = await _repository.GetLatestVitalsAsync(ct).ConfigureAwait(false);
        if (latest.Count > 0)
            return false;

        await _repository.IngestBatchAsync(GenerateSeedEvents(), ct).ConfigureAwait(false);
        return true;
    }

    internal List<BiometricEvent> GenerateSeedEvents()
    {
        var events = new List<BiometricEvent>();
        var now = _timeProvider.GetUtcNow();
        // Align to the top of the hour so the 15-minute grid is stable within a run.
        var startTime = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)
            .AddDays(-SeedDays);
        var random = new Random(RandomSeed);
        var source = new SourceMetadata(
            DeviceId: SeedDeviceId,
            Vendor: SeedVendor,
            FirmwareVersion: null,
            ConfidenceScore: 1.0f,
            IngestionTimestamp: now);

        for (int dayOffset = 0; dayOffset < SeedDays; dayOffset++)
        {
            var dayStart = startTime.AddDays(dayOffset);
            var dayOfWeek = dayStart.DayOfWeek;
            bool isRestDay = dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            // Heart Rate with circadian pattern
            for (int hour = 0; hour < 24; hour++)
            {
                double circadianFactor = Math.Cos((hour - 3) * Math.PI / 12) * 0.15 + 1;
                double baseHr = isRestDay ? 58 : 65;
                double hr = baseHr * circadianFactor + random.NextDouble() * 8 - 4;
                hr = Math.Clamp(hr, 45, 160);

                for (int minute = 0; minute < 60; minute += 15)
                {
                    events.Add(CreateBiometricEvent(
                        BiometricType.HeartRate,
                        dayStart.AddHours(hour).AddMinutes(minute),
                        hr + (random.NextDouble() * 3 - 1.5),
                        source));
                }
            }

            // HRV
            for (int hour = 0; hour < 24; hour++)
            {
                double baseHrv = isRestDay ? 85 : 55;
                double hrv = baseHrv + random.NextDouble() * 40 - 20;
                hrv = Math.Clamp(hrv, 15, 200);

                for (int minute = 0; minute < 60; minute += 15)
                {
                    events.Add(CreateBiometricEvent(
                        BiometricType.HeartRateVariability,
                        dayStart.AddHours(hour).AddMinutes(minute),
                        hrv + (random.NextDouble() * 20 - 10),
                        source));
                }
            }

            // SpO2
            const double baseSpO2 = 98.5;
            for (int hour = 0; hour < 24; hour++)
            {
                bool isSleep = hour >= 22 || hour < 6;
                double spo2 = baseSpO2 - (isSleep ? 1.5 : 0) + (random.NextDouble() * 1 - 0.5);
                spo2 = Math.Clamp(spo2, 94, 100);

                for (int minute = 0; minute < 60; minute += 30)
                {
                    events.Add(CreateBiometricEvent(
                        BiometricType.SpO2,
                        dayStart.AddHours(hour).AddMinutes(minute),
                        spo2,
                        source));
                }
            }

            // Recovery Score
            double recoveryTrajectory = 50 + (dayOfWeek switch
            {
                DayOfWeek.Sunday => 18,
                DayOfWeek.Monday => -8,
                DayOfWeek.Saturday => 12,
                _ => (dayOfWeek - DayOfWeek.Monday) % 4 * 2
            });
            double recovery = Math.Clamp(recoveryTrajectory + (random.NextDouble() * 15 - 7.5), 15, 95);
            events.Add(CreateBiometricEvent(BiometricType.RecoveryScore, dayStart.AddHours(6), recovery, source));

            // Readiness Score
            double readiness = Math.Clamp(
                recovery * 0.6 + (isRestDay ? 15 : -10) + (random.NextDouble() * 20 - 10),
                10, 98);
            events.Add(CreateBiometricEvent(BiometricType.ReadinessScore, dayStart.AddHours(7), readiness, source));

            // Strain Score
            double strain = isRestDay ? random.NextDouble() * 2 : 5 + random.NextDouble() * 12;
            strain = Math.Clamp(strain, 0, 21);
            events.Add(CreateBiometricEvent(BiometricType.StrainScore, dayStart.AddHours(20), strain, source));

            // Sleep Efficiency
            double sleepEfficiency = 85 + (isRestDay ? 5 : -3) + (random.NextDouble() * 8 - 4);
            sleepEfficiency = Math.Clamp(sleepEfficiency, 65, 99);
            events.Add(CreateBiometricEvent(BiometricType.SleepEfficiency, dayStart.AddHours(8), sleepEfficiency, source));

            // Sleep Duration
            double sleepDuration = 7.5 + (isRestDay ? 0.5 : -0.3) + (random.NextDouble() * 1 - 0.5);
            sleepDuration = Math.Clamp(sleepDuration, 5, 10);
            events.Add(CreateBiometricEvent(BiometricType.SleepDuration, dayStart.AddHours(8), sleepDuration, source));
        }

        return events;
    }

    /// <summary>
    /// Builds an event whose Id is derived from (type, timestamp) with no hashing,
    /// so two distinct seed points can never collide on the primary key.
    /// </summary>
    private static BiometricEvent CreateBiometricEvent(
        BiometricType type,
        DateTimeOffset timestamp,
        double value,
        SourceMetadata source)
    {
        Span<byte> idBytes = stackalloc byte[16];
        idBytes[0] = 0xA5; // seed-data marker
        idBytes[1] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(idBytes[8..], timestamp.UtcTicks);

        return new BiometricEvent(
            Id: new Guid(idBytes),
            Timestamp: timestamp,
            Type: type,
            Value: value,
            Unit: BiometricUnits.TryGetValue(type, out var unit) ? unit : "unknown",
            Source: source,
            CorrelationId: null);
    }
}
