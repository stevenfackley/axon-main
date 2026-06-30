using Axon.Core.Domain;
using Axon.Infrastructure.Drivers;

namespace Axon.Tests.Drivers;

/// <summary>
/// Coverage for the shared, pure <see cref="DriverUtilities"/> helpers used by every
/// vendor NormalizationMapper: deterministic (UUID v5) ID derivation and
/// <see cref="SourceMetadata"/> construction.
/// </summary>
public sealed class DriverUtilitiesTests
{
    private static readonly DateTimeOffset Ts =
        new(2026, 1, 15, 7, 0, 0, TimeSpan.Zero);

    // ── DeterministicId ─────────────────────────────────────────────────────────

    [Fact]
    public void DeterministicId_SameInputs_ProduceSameId()
    {
        var a = DriverUtilities.DeterministicId("Whoop", "dev-1", Ts, BiometricType.HeartRate);
        var b = DriverUtilities.DeterministicId("Whoop", "dev-1", Ts, BiometricType.HeartRate);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DeterministicId_DifferentType_ProducesDifferentId()
    {
        var hr = DriverUtilities.DeterministicId("Whoop", "dev-1", Ts, BiometricType.HeartRate);
        var hrv = DriverUtilities.DeterministicId("Whoop", "dev-1", Ts, BiometricType.HeartRateVariability);
        Assert.NotEqual(hr, hrv);
    }

    [Fact]
    public void DeterministicId_DifferentDevice_ProducesDifferentId()
    {
        var a = DriverUtilities.DeterministicId("Whoop", "dev-A", Ts, BiometricType.HeartRate);
        var b = DriverUtilities.DeterministicId("Whoop", "dev-B", Ts, BiometricType.HeartRate);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeterministicId_DifferentVendor_ProducesDifferentId()
    {
        var a = DriverUtilities.DeterministicId("Whoop", "dev-1", Ts, BiometricType.HeartRate);
        var b = DriverUtilities.DeterministicId("Garmin", "dev-1", Ts, BiometricType.HeartRate);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeterministicId_IsUuidVersion5()
    {
        var id = DriverUtilities.DeterministicId("Whoop", "dev-1", Ts, BiometricType.HeartRate);
        // RFC 4122 §4.1.3: the version nibble (high nibble of the 7th byte / 'M' digit) is 5.
        Assert.Equal('5', id.ToString()[14]);
    }

    // ── GuidV5 (RFC 4122 published vector) ──────────────────────────────────────

    [Fact]
    public void GuidV5_MatchesPublishedDnsVector()
    {
        // Canonical RFC 4122 v5 test vector:
        //   namespace = DNS (6ba7b810-9dad-11d1-80b4-00c04fd430c8), name = "www.example.com"
        //   → 2ed6657d-e927-568b-95e1-2665a8aea6a2
        var dnsNamespace = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        var result = GuidV5.Create(dnsNamespace, "www.example.com");
        Assert.Equal(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"), result);
    }

    // ── BuildSource ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildSource_PopulatesAllProvenanceFields()
    {
        var before = DateTimeOffset.UtcNow;
        var source = DriverUtilities.BuildSource("Whoop", "dev-1", 0.9f, "fw-2.1");

        Assert.Equal("dev-1", source.DeviceId);
        Assert.Equal("Whoop", source.Vendor);
        Assert.Equal("fw-2.1", source.FirmwareVersion);
        Assert.Equal(0.9f, source.ConfidenceScore);
        Assert.True(source.IngestionTimestamp >= before);
    }

    [Fact]
    public void BuildSource_FirmwareDefaultsToNull()
    {
        var source = DriverUtilities.BuildSource("Oura", "ring-1", 0.95f);
        Assert.Null(source.FirmwareVersion);
    }
}
