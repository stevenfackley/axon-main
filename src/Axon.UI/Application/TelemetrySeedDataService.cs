using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.UI.Application;

internal sealed class TelemetrySeedDataService(IBiometricRepository repository)
{
    private const int HistoryDays = 180;

    public async ValueTask EnsureSeedDataAsync(CancellationToken ct = default)
    {
        var latest = await repository.GetLatestVitalsAsync(ct);
        if (latest.Count > 0)
        {
            return;
        }

        var events = BuildEvents();
        await repository.AddRangeAsync(events, ct);
    }

    private static IReadOnlyList<BiometricEvent> BuildEvents()
    {
        var source = new SourceMetadata(
            DeviceId: "AXON-SEED-01",
            Vendor: "AxonSeed",
            FirmwareVersion: "seed-1.0",
            ConfidenceScore: 0.99f,
            IngestionTimestamp: DateTimeOffset.UtcNow);

        var rng = new Random(19790329);
        var now = DateTimeOffset.UtcNow;
        var baseDay = now.Date.AddDays(-HistoryDays);
        var events = new List<BiometricEvent>(HistoryDays * 80);

        for (int day = 0; day < HistoryDays; day++)
        {
            var dayStart = new DateTimeOffset(baseDay.AddDays(day), TimeSpan.Zero);

            double circadian = Math.Sin(day / 6.5d);
            double loadWave = Math.Cos(day / 11d);
            double sleepEfficiency = Clamp(78 + circadian * 8 + NextJitter(rng, 4), 60, 97);
            double strain = Clamp(43 + loadWave * 18 + NextJitter(rng, 8), 12, 92);
            double recovery = Clamp((sleepEfficiency * 0.65d) + ((100d - strain) * 0.35d) + NextJitter(rng, 3), 20, 98);
            double readiness = Clamp((sleepEfficiency * 0.55d) + ((100d - strain) * 0.25d) + (recovery * 0.20d), 15, 99);
            double sleepDurationSeconds = Clamp((7.2d + Math.Sin(day / 8d) * 0.9d + NextJitter(rng, 0.45d)) * 3600d, 5.3d * 3600d, 9.1d * 3600d);
            double spo2Baseline = Clamp(97.3d + NextJitter(rng, 0.6d), 95.0d, 99.5d);

            bool stressDay = day % 37 == 0 || day % 53 == 0;
            if (stressDay)
            {
                strain = Clamp(strain + 24d, 12, 98);
                recovery = Clamp(recovery - 18d, 5, 98);
                readiness = Clamp(readiness - 14d, 5, 99);
                sleepEfficiency = Clamp(sleepEfficiency - 10d, 50, 97);
                spo2Baseline = Clamp(spo2Baseline - 1.2d, 92, 99.5d);
            }

            events.Add(MakeEvent(dayStart.AddHours(6), BiometricType.SleepEfficiency, sleepEfficiency, "%", source));
            events.Add(MakeEvent(dayStart.AddHours(6).AddMinutes(5), BiometricType.SleepDuration, sleepDurationSeconds, "s", source));
            events.Add(MakeEvent(dayStart.AddHours(7), BiometricType.StrainScore, strain, "score", source));
            events.Add(MakeEvent(dayStart.AddHours(7).AddMinutes(5), BiometricType.RecoveryScore, recovery, "%", source));
            events.Add(MakeEvent(dayStart.AddHours(7).AddMinutes(10), BiometricType.ReadinessScore, readiness, "%", source));
            events.Add(MakeEvent(dayStart.AddHours(7).AddMinutes(15), BiometricType.SpO2, spo2Baseline, "%", source));

            for (int slot = 0; slot < 48; slot++)
            {
                var ts = dayStart.AddMinutes(slot * 30);
                double fractionOfDay = slot / 48d;
                double heartRate = 60
                    + Math.Sin(fractionOfDay * Math.PI * 2d) * 12d
                    + Math.Max(0d, Math.Sin((fractionOfDay - 0.28d) * Math.PI * 4d)) * 32d
                    + NextJitter(rng, 3.5d);
                double hrv = 64
                    + Math.Cos(fractionOfDay * Math.PI * 2d) * 8d
                    - (strain - 40d) * 0.18d
                    + NextJitter(rng, 4d);

                if (stressDay && slot is >= 18 and <= 26)
                {
                    heartRate += 24d;
                    hrv -= 18d;
                }

                events.Add(MakeEvent(ts, BiometricType.HeartRate, Clamp(heartRate, 42, 192), "bpm", source));

                if (slot % 4 == 0)
                {
                    events.Add(MakeEvent(ts.AddMinutes(10), BiometricType.HeartRateVariability, Clamp(hrv, 18, 120), "ms", source));
                }
            }
        }

        return events;
    }

    private static BiometricEvent MakeEvent(
        DateTimeOffset timestamp,
        BiometricType type,
        double value,
        string unit,
        SourceMetadata source) =>
        new(
            Id: Guid.NewGuid(),
            Timestamp: timestamp,
            Type: type,
            Value: value,
            Unit: unit,
            Source: source);

    private static double NextJitter(Random rng, double amplitude) =>
        ((rng.NextDouble() * 2d) - 1d) * amplitude;

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));
}
