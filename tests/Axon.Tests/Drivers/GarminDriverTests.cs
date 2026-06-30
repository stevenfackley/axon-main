using System.Net;
using System.Text.Json;
using Axon.Core.Domain;
using Axon.Infrastructure.Drivers.Garmin;
using Microsoft.Extensions.Logging.Abstractions;

namespace Axon.Tests.Drivers;

/// <summary>
/// Adapter-level coverage for <see cref="GarminDriver"/>: offline file import
/// (name routing + deserialisation through <see cref="GarminNormalizationMapper"/>),
/// the OAuth-gated HTTP fetch path, and error/empty/availability handling.
/// </summary>
public sealed class GarminDriverTests : IDisposable
{
    private const string DriverId = "garmin";

    // Unix epoch 2026-01-15T00:00:00Z
    private const long EpochBase = 1_768_521_600L;

    private readonly TempWorkspace _temp = new();

    public void Dispose() => _temp.Dispose();

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static GarminDailySummary SampleDaily() =>
        new("user1", "token1", EpochBase, EpochBase + 86400, "sum-1", "ACTIVITY",
            EpochBase, 0, 86400,
            8000, null, null, 450f, 1900f, null, 72, 155, 52, null,
            35, null, null, null, 97.5f, null);

    private static GarminSleepSummary SampleSleep() =>
        new("user1", "sleep-1", "2026-01-15", EpochBase, 0, 27000,
            null, 5400, 10800, 7200, null, null, null, null, null, 96.5f,
            null, null, 14.3f, null, null, 20, null);

    private static GarminHrvSummary SampleHrv() =>
        new("user1", "hrv-1", "2026-01-15", EpochBase,
            new GarminHrvLastNight(60, 55, 58, null), null);

    private static GarminBodyComposition SampleBody() =>
        new("user1", "body-1", EpochBase, 0, 35_000, 3_500, 55f, 18f, null, 80_000);

    private static GarminDriver BuildDriver(InMemoryOAuthTokenStore store, HttpClient http) =>
        BuildDriver(store, http, new GarminDriverOptions());

    private static GarminDriver BuildDriver(
        InMemoryOAuthTokenStore store, HttpClient http, GarminDriverOptions options) =>
        new(store, http, options, NullLogger<GarminDriver>.Instance);

    private static GarminDriver OfflineDriver() =>
        BuildDriver(new InMemoryOAuthTokenStore(), new HttpClient(StubHttpMessageHandler.ThrowingOnUse()));

    // ── Identity ────────────────────────────────────────────────────────────────

    [Fact]
    public void Identity_MatchesGarmin()
    {
        var driver = OfflineDriver();
        Assert.Equal(DriverId, driver.DriverId);
        Assert.Equal("Garmin Connect", driver.DisplayName);
        Assert.False(driver.SupportsOffline);
    }

    // ── ImportFileAsync (offline) ───────────────────────────────────────────────

    [Fact]
    public async Task ImportFile_Daily_EmitsEventsWithVendorAndCorrelation()
    {
        var json = JsonSerializer.Serialize(new GarminDailySummaryList([SampleDaily()]));
        var path = _temp.WriteFile("daily.json", json);

        var events = await DriverTestHelpers.CollectAsync(
            OfflineDriver().ImportFileAsync(path, correlationId: "imp-g"));

        Assert.Contains(events, e => e.Type == BiometricType.Steps);
        Assert.All(events, e => Assert.Equal("Garmin", e.Source.Vendor));
        Assert.All(events, e => Assert.Equal("imp-g", e.CorrelationId));
    }

    [Fact]
    public async Task ImportFile_Sleep_EmitsSleepDuration()
    {
        var json = JsonSerializer.Serialize(new GarminSleepSummaryList([SampleSleep()]));
        var path = _temp.WriteFile("sleep.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.SleepDuration);
    }

    [Fact]
    public async Task ImportFile_Hrv_EmitsHrvEvents()
    {
        var json = JsonSerializer.Serialize(new GarminHrvSummaryList([SampleHrv()]));
        var path = _temp.WriteFile("hrv.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.HeartRateVariability);
    }

    [Fact]
    public async Task ImportFile_Body_EmitsBodyWeight()
    {
        var json = JsonSerializer.Serialize(new GarminBodyCompositionList([SampleBody()]));
        var path = _temp.WriteFile("body.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.BodyWeight);
    }

    [Fact]
    public async Task ImportFile_UnrecognisedName_YieldsNothing()
    {
        var json = JsonSerializer.Serialize(new GarminDailySummaryList([SampleDaily()]));
        var path = _temp.WriteFile("mystery.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Empty(events);
    }

    [Fact]
    public async Task ImportFile_EmptyList_YieldsNothing()
    {
        var json = JsonSerializer.Serialize(new GarminDailySummaryList([]));
        var path = _temp.WriteFile("daily.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Empty(events);
    }

    // ── Availability / Authorisation ────────────────────────────────────────────

    [Fact]
    public async Task IsAvailable_FalseWhenConsumerKeyBlank()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new GarminDriverOptions { ConsumerKey = "" });
        Assert.False(await driver.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_TrueWhenConsumerKeyConfigured()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new GarminDriverOptions { ConsumerKey = "real-key" });
        Assert.True(await driver.IsAvailableAsync());
    }

    [Fact]
    public async Task Authorise_ThrowsWhenNoToken()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await OfflineDriver().AuthoriseAsync());
    }

    [Fact]
    public async Task Authorise_SucceedsWithStoredToken()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var driver = BuildDriver(store, new HttpClient(StubHttpMessageHandler.ThrowingOnUse()));

        await driver.AuthoriseAsync(); // must not throw
    }

    // ── FetchSinceAsync (HTTP) ──────────────────────────────────────────────────

    private static HttpResponseMessage GarminHappyResponder(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("dailies", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(
                JsonSerializer.Serialize(new GarminDailySummaryList([SampleDaily()])));
        if (path.EndsWith("sleeps", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json("""{"sleeps":[]}""");
        if (path.EndsWith("hrv", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json("""{"hrvSummaries":[]}""");
        return StubHttpMessageHandler.Json("""{"compositions":[]}""");
    }

    [Fact]
    public async Task Fetch_WithValidToken_EmitsMappedEventsFromApi()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(GarminHappyResponder);
        var driver = BuildDriver(store, new HttpClient(handler));

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-7)));

        Assert.Contains(events, e => e.Type == BiometricType.Steps);
        Assert.All(events, e => Assert.Equal("Garmin", e.Source.Vendor));
        Assert.Equal(4, handler.RequestPaths.Count); // dailies, sleeps, hrv, bodyComps
    }

    [Fact]
    public async Task Fetch_NoToken_YieldsNothingAndMakesNoCall()
    {
        var handler = StubHttpMessageHandler.ThrowingOnUse();
        var driver = BuildDriver(new InMemoryOAuthTokenStore(), new HttpClient(handler));

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
        Assert.Empty(handler.RequestPaths);
    }

    [Fact]
    public async Task Fetch_HttpError_IsSwallowedAndYieldsNothing()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Status(HttpStatusCode.BadGateway));
        var driver = BuildDriver(store, new HttpClient(handler));

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
    }
}
