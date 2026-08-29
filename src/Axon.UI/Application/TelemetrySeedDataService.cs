using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.UI.Application;

/// <summary>
/// Generates deterministic seed data for local testing and development.
/// Creates a realistic 30-day time series across all relevant biometric types.
/// Safe to call on every app startup; skips if data already exists.
/// </summary>
internal sealed class TelemetrySeedDataService
{
    private readonly IBiometricRepository _repository;
    private static readonly TimeSpan SeedWindowStart = TimeSpan.FromDays(-30);

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

    public TelemetrySeedDataService(IBiometricRepository repository)
    {
        _repository = repository;
    }

    public async ValueTask<bool> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        var existing = await _repository.QueryRangeAsync(
            BiometricType.HeartRate,
            DateTimeOffset.UtcNow.AddDays(-365),
            DateTimeOffset.UtcNow,
            ct);

        if (existing.Count > 0)
            return false;

        var events = GenerateSeedEvents();
        await _repository.IngestBatchAsync(events, ct);
        return true;
    }

    private List<BiometricEvent> GenerateSeedEvents()
    {
        var events = new List<BiometricEvent>();
        var now = DateTimeOffset.UtcNow;
        var startTime = now + SeedWindowStart;
        var random = new Random(seed: 42);

        for (int dayOffset = 0; dayOffset < 30; dayOffset++)
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
                        random));
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
                        random));
                }
            }

            // SpO2
            double baseSpO2 = 98.5;
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
                        random));
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
            events.Add(CreateBiometricEvent(BiometricType.RecoveryScore, dayStart.AddHours(6), recovery, random));

            // Readiness Score
            double readiness = Math.Clamp(
                recovery * 0.6 + (isRestDay ? 15 : -10) + (random.NextDouble() * 20 - 10),
                10, 98);
            events.Add(CreateBiometricEvent(BiometricType.ReadinessScore, dayStart.AddHours(7), readiness, random));

            // Strain Score
            double strain = isRestDay ? random.NextDouble() * 2 : 5 + random.NextDouble() * 12;
            strain = Math.Clamp(strain, 0, 21);
            events.Add(CreateBiometricEvent(BiometricType.StrainScore, dayStart.AddHours(20), strain, random));

            // Sleep Efficiency
            double sleepEfficiency = 85 + (isRestDay ? 5 : -3) + (random.NextDouble() * 8 - 4);
            sleepEfficiency = Math.Clamp(sleepEfficiency, 65, 99);
            events.Add(CreateBiometricEvent(BiometricType.SleepEfficiency, dayStart.AddHours(8), sleepEfficiency, random));

            // Sleep Duration
            double sleepDuration = 7.5 + (isRestDay ? 0.5 : -0.3) + (random.NextDouble() * 1 - 0.5);
            sleepDuration = Math.Clamp(sleepDuration, 5, 10);
            events.Add(CreateBiometricEvent(BiometricType.SleepDuration, dayStart.AddHours(8), sleepDuration, random));
        }

        return events;
    }

    private static BiometricEvent CreateBiometricEvent(
        BiometricType type,
        DateTimeOffset timestamp,
        double value,
        Random random)
    {
        var idBytes = new byte[16];
        var typeHash = type.GetHashCode();
        var tsHash = timestamp.UtcTicks.GetHashCode();
        var combined = (typeHash ^ tsHash).GetHashCode();

        var guidRandom = new Random(combined);
        guidRandom.NextBytes(idBytes);
        var id = new Guid(idBytes);

        return new BiometricEvent(
            Id: id,
            Timestamp: timestamp,
            Type: type,
            Value: value,
            Unit: BiometricUnits.TryGetValue(type, out var unit) ? unit : "unknown",
            Source: new SourceMetadata(
                SourceType: "seed-data",
                DeviceId: "local-dev-seed",
                DeviceName: "Development Seed Data"),
            CorrelationId: null);
    }
}
