using Axon.Core.Domain;
using Axon.Infrastructure.Drivers;
using Axon.Infrastructure.Drivers.Garmin;
using Axon.Infrastructure.Drivers.Oura;
using Axon.Infrastructure.Drivers.Whoop;

namespace Axon.Tests.Drivers;

/// <summary>
/// Pins the event envelope every vendor NormalizationMapper builds around a value:
/// the deterministic Id inputs (vendor, device, timestamp, type), the provenance
/// (vendor, device, confidence, no firmware), the correlation id, and the round-trip
/// timestamp parsing that keeps the vendor's UTC offset (the offset is part of the Id).
/// </summary>
public sealed class NormalizationMapperEnvelopeTests
{
    [Fact]
    public void Whoop_Recovery_EnvelopeAndOffsetArePreserved()
    {
        const string createdAt = "2026-01-15T07:00:00.000+02:00";
        var recovery = new WhoopRecovery(1, 100, 200, 42, createdAt, createdAt, "SCORED",
            new WhoopRecoveryScore(false, 75f, 52f, 68f, null, null));

        var events = WhoopNormalizationMapper.MapRecovery(recovery, "whoop-dev", "batch-1").ToList();

        AssertEnvelope(events, "Whoop", 0.92f, "whoop-dev", "batch-1");
        Assert.All(events, e =>
        {
            Assert.Equal(TimeSpan.FromHours(2), e.Timestamp.Offset);
            Assert.Equal(new DateTimeOffset(2026, 1, 15, 5, 0, 0, TimeSpan.Zero), e.Timestamp);
        });
    }

    [Fact]
    public void Whoop_BodyWeight_EnvelopeUsesSuppliedTimestamp()
    {
        var measuredAt = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

        var evt = WhoopNormalizationMapper.MapBodyWeight(
            new WhoopBodyMeasurement(1.8f, 80f, 190), "whoop-dev", measuredAt);

        AssertEnvelope([evt], "Whoop", 0.92f, "whoop-dev", null);
        Assert.Equal(measuredAt, evt.Timestamp);
    }

    [Fact]
    public void Oura_Readiness_EnvelopeAndOffsetArePreserved()
    {
        var readiness = new OuraDailyReadiness(
            "ring-record-1", "2026-01-15", Score: 85, TemperatureDeviation: 0.2f,
            TemperatureTrendDeviation: null,
            Timestamp: "2026-01-15T00:00:00-05:00", Contributors: null);

        var events = OuraNormalizationMapper.MapDailyReadiness(readiness, "batch-2").ToList();

        AssertEnvelope(events, "Oura", 0.93f, "ring-record-1", "batch-2");
        Assert.All(events, e => Assert.Equal(TimeSpan.FromHours(-5), e.Timestamp.Offset));
    }

    [Fact]
    public void Oura_HeartRateSample_EnvelopeCarriesRingAndSource()
    {
        var sample = new OuraHeartRateSample(61, "rest", "2026-01-15T03:00:00+00:00");

        var evt = OuraNormalizationMapper.MapHeartRateSample(sample, "ring-9", "batch-3");

        AssertEnvelope([evt], "Oura", 0.93f, "ring-9:rest", "batch-3");
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero), evt.Timestamp);
    }

    [Fact]
    public void Garmin_DailySummary_EnvelopeIsPinned()
    {
        const long epoch = 1_768_521_600L; // 2026-01-15T00:00:00Z
        var summary = new GarminDailySummary("user1", "token1", epoch, epoch + 86400, "sum-1", "ACTIVITY",
            epoch, 3600, 86400,
            8000, null, null, 450f, 1900f, null, 72, 155, 52, null,
            35, null, null, null, 97.5f, null);

        var events = GarminNormalizationMapper.MapDailySummary(summary, "batch-4").ToList();

        AssertEnvelope(events, "Garmin", 0.90f, "sum-1", "batch-4");
        Assert.All(events, e => Assert.Equal(TimeSpan.FromHours(1), e.Timestamp.Offset));
    }

    private static void AssertEnvelope(
        IReadOnlyList<BiometricEvent> events,
        string vendor,
        float confidence,
        string deviceId,
        string? correlationId)
    {
        Assert.NotEmpty(events);
        Assert.All(events, e =>
        {
            Assert.Equal(DriverUtilities.DeterministicId(vendor, deviceId, e.Timestamp, e.Type), e.Id);
            Assert.Equal(vendor, e.Source.Vendor);
            Assert.Equal(deviceId, e.Source.DeviceId);
            Assert.Equal(confidence, e.Source.ConfidenceScore);
            Assert.Null(e.Source.FirmwareVersion);
            Assert.Equal(correlationId, e.CorrelationId);
        });
    }
}
