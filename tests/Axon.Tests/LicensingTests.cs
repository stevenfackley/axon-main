using Axon.Core.Licensing;

namespace Axon.Tests;

/// <summary>
/// Full coverage of the feature-gate matrix and offline license-key validation.
/// </summary>
public sealed class LicensingTests
{
    // ── FeatureGate – Free tier ───────────────────────────────────────────────

    [Fact]
    public void Free_Denies_ApiSync()
        => Assert.False(FeatureGate.IsAllowed(Feature.ApiSync, LicenseTier.Free));

    [Fact]
    public void Free_Denies_UnlimitedHistory()
        => Assert.False(FeatureGate.IsAllowed(Feature.UnlimitedHistory, LicenseTier.Free));

    [Fact]
    public void Free_Denies_MlInsightEngine()
        => Assert.False(FeatureGate.IsAllowed(Feature.MlInsightEngine, LicenseTier.Free));

    [Fact]
    public void Free_Denies_CorrelationLab()
        => Assert.False(FeatureGate.IsAllowed(Feature.CorrelationLab, LicenseTier.Free));

    [Fact]
    public void Free_Denies_ShareableExports()
        => Assert.False(FeatureGate.IsAllowed(Feature.ShareableExports, LicenseTier.Free));

    [Fact]
    public void Free_Denies_MultiSource()
        => Assert.False(FeatureGate.IsAllowed(Feature.MultiSource, LicenseTier.Free));

    [Fact]
    public void Free_Denies_SovereignSync()
        => Assert.False(FeatureGate.IsAllowed(Feature.SovereignSync, LicenseTier.Free));

    [Fact]
    public void Free_Allows_ManualImport()
        => Assert.True(FeatureGate.IsAllowed(Feature.ManualImport, LicenseTier.Free));

    [Fact]
    public void Free_Allows_BasicVisualization()
        => Assert.True(FeatureGate.IsAllowed(Feature.BasicVisualization, LicenseTier.Free));

    [Fact]
    public void Free_Allows_TwelveMonthHistory()
        => Assert.True(FeatureGate.IsAllowed(Feature.TwelveMonthHistory, LicenseTier.Free));

    // ── FeatureGate – Pro tier ────────────────────────────────────────────────

    [Fact]
    public void Pro_Allows_ApiSync()
        => Assert.True(FeatureGate.IsAllowed(Feature.ApiSync, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_UnlimitedHistory()
        => Assert.True(FeatureGate.IsAllowed(Feature.UnlimitedHistory, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_MlInsightEngine()
        => Assert.True(FeatureGate.IsAllowed(Feature.MlInsightEngine, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_CorrelationLab()
        => Assert.True(FeatureGate.IsAllowed(Feature.CorrelationLab, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_ShareableExports()
        => Assert.True(FeatureGate.IsAllowed(Feature.ShareableExports, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_MultiSource()
        => Assert.True(FeatureGate.IsAllowed(Feature.MultiSource, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_SovereignSync()
        => Assert.True(FeatureGate.IsAllowed(Feature.SovereignSync, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_ManualImport()
        => Assert.True(FeatureGate.IsAllowed(Feature.ManualImport, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_BasicVisualization()
        => Assert.True(FeatureGate.IsAllowed(Feature.BasicVisualization, LicenseTier.Pro));

    [Fact]
    public void Pro_Allows_TwelveMonthHistory()
        => Assert.True(FeatureGate.IsAllowed(Feature.TwelveMonthHistory, LicenseTier.Pro));

    // ── FeatureGate – Lifetime tier ───────────────────────────────────────────

    [Fact]
    public void Lifetime_Allows_ApiSync()
        => Assert.True(FeatureGate.IsAllowed(Feature.ApiSync, LicenseTier.Lifetime));

    [Fact]
    public void Lifetime_Allows_UnlimitedHistory()
        => Assert.True(FeatureGate.IsAllowed(Feature.UnlimitedHistory, LicenseTier.Lifetime));

    [Fact]
    public void Lifetime_Allows_MlInsightEngine()
        => Assert.True(FeatureGate.IsAllowed(Feature.MlInsightEngine, LicenseTier.Lifetime));

    [Fact]
    public void Lifetime_Allows_CorrelationLab()
        => Assert.True(FeatureGate.IsAllowed(Feature.CorrelationLab, LicenseTier.Lifetime));

    [Fact]
    public void Lifetime_Allows_ShareableExports()
        => Assert.True(FeatureGate.IsAllowed(Feature.ShareableExports, LicenseTier.Lifetime));

    [Fact]
    public void Lifetime_Allows_MultiSource()
        => Assert.True(FeatureGate.IsAllowed(Feature.MultiSource, LicenseTier.Lifetime));

    [Fact]
    public void Lifetime_Allows_SovereignSync()
        => Assert.True(FeatureGate.IsAllowed(Feature.SovereignSync, LicenseTier.Lifetime));

    // ── LicenseKey – happy path ───────────────────────────────────────────────

    [Fact]
    public void ValidKey_Pro_ReturnsTrue_AndTierIsPro()
    {
        var key = LicenseKey.MintForTesting(LicenseTier.Pro, DateTimeOffset.UtcNow.AddDays(30));
        var result = LicenseKey.TryValidate(key, out var tier);
        Assert.True(result);
        Assert.Equal(LicenseTier.Pro, tier);
    }

    [Fact]
    public void ValidKey_Lifetime_ReturnsTrue_AndTierIsLifetime()
    {
        var key = LicenseKey.MintForTesting(LicenseTier.Lifetime, DateTimeOffset.UtcNow.AddYears(100));
        var result = LicenseKey.TryValidate(key, out var tier);
        Assert.True(result);
        Assert.Equal(LicenseTier.Lifetime, tier);
    }

    [Fact]
    public void ValidKey_Free_ReturnsTrue_AndTierIsFree()
    {
        var key = LicenseKey.MintForTesting(LicenseTier.Free, DateTimeOffset.UtcNow.AddDays(1));
        var result = LicenseKey.TryValidate(key, out var tier);
        Assert.True(result);
        Assert.Equal(LicenseTier.Free, tier);
    }

    // ── LicenseKey – rejection ────────────────────────────────────────────────

    [Fact]
    public void ExpiredKey_ReturnsFalse()
    {
        var key = LicenseKey.MintForTesting(LicenseTier.Pro, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.False(LicenseKey.TryValidate(key, out _));
    }

    [Fact]
    public void TamperedKey_ReturnsFalse()
    {
        var key = LicenseKey.MintForTesting(LicenseTier.Pro, DateTimeOffset.UtcNow.AddDays(30));
        // Flip one character in the payload segment (before the last dot which is the HMAC)
        var parts = key.Split('.');
        Assert.True(parts.Length >= 2, "key should have at least payload.hmac segments");
        var badPayload = parts[0].ToCharArray();
        badPayload[0] = badPayload[0] == 'A' ? 'B' : 'A';
        parts[0] = new string(badPayload);
        var tampered = string.Join('.', parts);
        Assert.False(LicenseKey.TryValidate(tampered, out _));
    }

    [Fact]
    public void MalformedKey_Empty_ReturnsFalse()
        => Assert.False(LicenseKey.TryValidate(string.Empty, out _));

    [Fact]
    public void MalformedKey_RandomString_ReturnsFalse()
        => Assert.False(LicenseKey.TryValidate("not-a-valid-key-at-all", out _));

    [Fact]
    public void MalformedKey_OnlyOneSegment_ReturnsFalse()
        => Assert.False(LicenseKey.TryValidate("onlyone", out _));

    [Fact]
    public void TamperedHmac_ReturnsFalse()
    {
        var key = LicenseKey.MintForTesting(LicenseTier.Pro, DateTimeOffset.UtcNow.AddDays(30));
        var parts = key.Split('.');
        // Corrupt last segment (HMAC)
        var badHmac = parts[^1].ToCharArray();
        badHmac[0] = badHmac[0] == 'A' ? 'B' : 'A';
        parts[^1] = new string(badHmac);
        Assert.False(LicenseKey.TryValidate(string.Join('.', parts), out _));
    }

    [Fact]
    public void Null_Key_ReturnsFalse()
        => Assert.False(LicenseKey.TryValidate(null!, out _));
}
