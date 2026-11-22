using System.Net;
using System.Text.Json;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Drivers.Garmin;
using Microsoft.Extensions.Logging;
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
    public async Task IsAvailable_FalseWithDefaultOptions()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new GarminDriverOptions());
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

    // ── Request shape, error paths ──────────────────────────────────────────────

    private static bool IsDailies(HttpRequestMessage req) =>
        req.RequestUri!.AbsolutePath.EndsWith("dailies", StringComparison.Ordinal);

    [Fact]
    public async Task Fetch_MakesOneRequestPerEndpointWithTheUploadWindowAndBearerToken()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var queries = new List<string>();
        var auth = new List<string?>();
        var handler = new StubHttpMessageHandler(req =>
        {
            queries.Add(req.RequestUri!.Query);
            auth.Add(req.Headers.Authorization?.ToString());
            return GarminHappyResponder(req);
        });
        var since = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        await DriverTestHelpers.CollectAsync(
            BuildDriver(store, new HttpClient(handler)).FetchSinceAsync(since));

        Assert.Equal(["/dailies", "/sleeps", "/hrv", "/bodyComps"],
            handler.RequestPaths.Select(p => p[p.LastIndexOf('/')..]));
        Assert.All(queries, q => Assert.StartsWith(
            "?uploadStartTimeInSeconds=1768003200&uploadEndTimeInSeconds=", q, StringComparison.Ordinal));
        Assert.All(auth, a => Assert.Equal("Bearer access-token", a));
    }

    [Fact]
    public async Task Fetch_ExpiredStoredToken_IsStillSent()
    {
        var expired = new OAuthTokenSet("old-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(-1));
        var store = new InMemoryOAuthTokenStore(DriverId, expired);
        var auth = new List<string?>();
        var handler = new StubHttpMessageHandler(req =>
        {
            auth.Add(req.Headers.Authorization?.ToString());
            return GarminHappyResponder(req);
        });

        await DriverTestHelpers.CollectAsync(
            BuildDriver(store, new HttpClient(handler)).FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Equal(4, auth.Count);
        Assert.All(auth, a => Assert.Equal("Bearer old-token", a));
    }

    [Fact]
    public async Task Fetch_WhenOneEndpointFails_LogsTheUrlAndFetchesTheRest()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(req => IsDailies(req)
            ? StubHttpMessageHandler.Status(HttpStatusCode.BadGateway)
            : GarminHappyResponder(req));
        var logger = new CapturingLogger<GarminDriver>();
        var driver = new GarminDriver(store, new HttpClient(handler), new GarminDriverOptions(), logger);

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
        Assert.Equal(4, handler.RequestPaths.Count);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("Garmin: API call failed for {Url}", error.Template);
        Assert.Contains("/dailies?uploadStartTimeInSeconds=", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_MalformedResponse_Throws()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("not json"));

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            DriverTestHelpers.CollectAsync(
                BuildDriver(store, new HttpClient(handler)).FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1))));
    }

    [Fact]
    public async Task ImportFile_NameMatchingTwoRoutes_UsesTheFirst()
    {
        var json = JsonSerializer.Serialize(new GarminDailySummaryList([SampleDaily()]));
        var path = _temp.WriteFile("sleep_daily.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.Steps);
    }

    [Fact]
    public async Task FetchAndImport_MatchPropertyNamesIgnoringCase()
    {
        var json = DriverTestHelpers.UpperCaseKeys(
            JsonSerializer.Serialize(new GarminDailySummaryList([SampleDaily()])));
        var path = _temp.WriteFile("daily.json", json);
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(req => IsDailies(req)
            ? StubHttpMessageHandler.Json(json)
            : GarminHappyResponder(req));

        var imported = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));
        var fetched = await DriverTestHelpers.CollectAsync(
            BuildDriver(store, new HttpClient(handler)).FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Contains(imported, e => e.Type == BiometricType.Steps);
        Assert.Contains(fetched, e => e.Type == BiometricType.Steps);
    }

    [Fact]
    public async Task ImportFile_WeightName_RoutesToBodyComposition()
    {
        var json = JsonSerializer.Serialize(new GarminBodyCompositionList([SampleBody()]));
        var path = _temp.WriteFile("weight.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.BodyWeight);
    }

    [Fact]
    public async Task ImportFile_UnrecognisedName_LogsAWarning()
    {
        var path = _temp.WriteFile("Mystery.json", "{}");
        var logger = new CapturingLogger<GarminDriver>();
        var driver = new GarminDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()), new GarminDriverOptions(), logger);

        await DriverTestHelpers.CollectAsync(driver.ImportFileAsync(path));

        Assert.Equal(
            [
                (LogLevel.Information, "Garmin: Importing file {File}", "Garmin: Importing file Mystery.json"),
                (LogLevel.Warning, "Garmin: Unrecognised export file name '{File}' — skipping.",
                    "Garmin: Unrecognised export file name 'mystery' — skipping."),
            ],
            logger.Entries);
    }

    [Fact]
    public async Task ImportFile_MalformedFile_Throws()
    {
        var path = _temp.WriteFile("hrv.json", "not json");

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path)));
    }
}
