using System.Net;
using System.Text.Json;
using Axon.Core.Domain;
using Axon.Infrastructure.Drivers.Oura;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Axon.Tests.Drivers;

/// <summary>
/// Adapter-level coverage for <see cref="OuraDriver"/>: offline file import,
/// the HTTP fetch path under both the Personal-Access-Token and stored-OAuth-token
/// modes, and error/empty/availability handling.
/// </summary>
public sealed class OuraDriverTests : IDisposable
{
    private const string DriverId = "oura";
    private readonly TempWorkspace _temp = new();

    public void Dispose() => _temp.Dispose();

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static OuraDailyReadiness SampleReadiness() =>
        new("rid-1", "2026-01-15", 85, null, null, "2026-01-15T00:00:00+00:00", null);

    private static OuraSleepSession SampleSleep() =>
        new("sess-1", 13.5f, 55f, 62, null,
            "2026-01-15T07:30:00+00:00", "2026-01-15T00:30:00+00:00", "2026-01-15",
            5400, 88, null, null, 420, 10800, false, 48, null, 0, null, null,
            7200, null, null, null, null, null, 25200, "long_sleep");

    private static OuraDailyActivity SampleActivity() =>
        new("act-1", null, 75, 500, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null,
            9000, null, null, 2200, "2026-01-15", "2026-01-15T12:00:00+00:00");

    private static OuraHeartRateSample SampleHeartRate() =>
        new(72, "rest", "2026-01-15T12:00:00+00:00");

    private static OuraSpO2Daily SampleSpO2() =>
        new("spo2-1", "2026-01-15", new OuraSpo2Percentage(98.5f, 95f, 99.5f));

    private static OuraDriver BuildDriver(InMemoryOAuthTokenStore store, HttpClient http) =>
        BuildDriver(store, http, new OuraDriverOptions());

    private static OuraDriver BuildDriver(
        InMemoryOAuthTokenStore store, HttpClient http, OuraDriverOptions options) =>
        new(store, http, options, NullLogger<OuraDriver>.Instance);

    private static OuraDriver OfflineDriver() =>
        BuildDriver(new InMemoryOAuthTokenStore(), new HttpClient(StubHttpMessageHandler.ThrowingOnUse()));

    // ── Identity ────────────────────────────────────────────────────────────────

    [Fact]
    public void Identity_MatchesOura()
    {
        var driver = OfflineDriver();
        Assert.Equal(DriverId, driver.DriverId);
        Assert.Equal("Oura Ring", driver.DisplayName);
        Assert.False(driver.SupportsOffline);
    }

    // ── ImportFileAsync (offline) ───────────────────────────────────────────────

    [Fact]
    public async Task ImportFile_Readiness_EmitsEventsWithVendorAndCorrelation()
    {
        var json = JsonSerializer.Serialize(new OuraDailyReadinessList([SampleReadiness()], null));
        var path = _temp.WriteFile("readiness.json", json);

        var events = await DriverTestHelpers.CollectAsync(
            OfflineDriver().ImportFileAsync(path, correlationId: "imp-o"));

        Assert.Contains(events, e => e.Type == BiometricType.ReadinessScore);
        Assert.All(events, e => Assert.Equal("Oura", e.Source.Vendor));
        Assert.All(events, e => Assert.Equal("imp-o", e.CorrelationId));
    }

    [Fact]
    public async Task ImportFile_Sleep_EmitsSleepDuration()
    {
        var json = JsonSerializer.Serialize(new OuraSleepSessionList([SampleSleep()], null));
        var path = _temp.WriteFile("sleep.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.SleepDuration);
    }

    [Fact]
    public async Task ImportFile_Activity_EmitsSteps()
    {
        var json = JsonSerializer.Serialize(new OuraDailyActivityList([SampleActivity()], null));
        var path = _temp.WriteFile("activity.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.Steps);
    }

    [Fact]
    public async Task ImportFile_HeartRate_EmitsHeartRate()
    {
        var json = JsonSerializer.Serialize(new OuraHeartRateList([SampleHeartRate()], null));
        var path = _temp.WriteFile("heartrate.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.HeartRate);
    }

    [Fact]
    public async Task ImportFile_SpO2_EmitsSpO2()
    {
        var json = JsonSerializer.Serialize(new OuraSpO2List([SampleSpO2()], null));
        var path = _temp.WriteFile("spo2.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.SpO2);
    }

    [Fact]
    public async Task ImportFile_UnrecognisedName_YieldsNothing()
    {
        var json = JsonSerializer.Serialize(new OuraDailyReadinessList([SampleReadiness()], null));
        var path = _temp.WriteFile("mystery.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Empty(events);
    }

    [Fact]
    public async Task ImportFile_EmptyList_YieldsNothing()
    {
        var json = JsonSerializer.Serialize(new OuraDailyReadinessList([], null));
        var path = _temp.WriteFile("readiness.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Empty(events);
    }

    // ── Availability / Authorisation ────────────────────────────────────────────

    [Fact]
    public async Task IsAvailable_FalseWhenNoCredentials()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new OuraDriverOptions { ClientId = "", PersonalAccessToken = null });
        Assert.False(await driver.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_FalseWithDefaultOptions()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new OuraDriverOptions());
        Assert.False(await driver.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_TrueWhenPatConfigured()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new OuraDriverOptions { ClientId = "", PersonalAccessToken = "pat-123" });
        Assert.True(await driver.IsAvailableAsync());
    }

    [Fact]
    public async Task Authorise_ThrowsWhenNoPatAndNoToken()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new OuraDriverOptions { PersonalAccessToken = null });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await driver.AuthoriseAsync());
    }

    [Fact]
    public async Task Authorise_SucceedsWithPat_NoTokenStoreNeeded()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new OuraDriverOptions { PersonalAccessToken = "pat-123" });

        await driver.AuthoriseAsync(); // must not throw, must not touch the store
    }

    // ── FetchSinceAsync (HTTP) ──────────────────────────────────────────────────

    private static HttpResponseMessage OuraHappyResponder(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("daily_readiness", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(
                JsonSerializer.Serialize(new OuraDailyReadinessList([SampleReadiness()], null)));
        // All other collections return a well-formed empty page.
        return StubHttpMessageHandler.Json("""{"data":[]}""");
    }

    [Fact]
    public async Task Fetch_WithStoredToken_EmitsMappedEventsFromApi()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(OuraHappyResponder);
        var driver = BuildDriver(store, new HttpClient(handler));

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-7)));

        Assert.Contains(events, e => e.Type == BiometricType.ReadinessScore);
        Assert.All(events, e => Assert.Equal("Oura", e.Source.Vendor));
        // readiness, sleep, daily_activity, heartrate, spo2
        Assert.Equal(5, handler.RequestPaths.Count);
    }

    [Fact]
    public async Task Fetch_WithPersonalAccessToken_WorksWithoutStoredToken()
    {
        var handler = new StubHttpMessageHandler(OuraHappyResponder);
        var driver = BuildDriver(new InMemoryOAuthTokenStore(), new HttpClient(handler),
            new OuraDriverOptions { PersonalAccessToken = "pat-123" });

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-7)));

        Assert.Contains(events, e => e.Type == BiometricType.ReadinessScore);
    }

    [Fact]
    public async Task Fetch_NoCredentials_YieldsNothingAndMakesNoCall()
    {
        var handler = StubHttpMessageHandler.ThrowingOnUse();
        var driver = BuildDriver(new InMemoryOAuthTokenStore(), new HttpClient(handler),
            new OuraDriverOptions { ClientId = "", PersonalAccessToken = null });

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
        Assert.Empty(handler.RequestPaths);
    }

    [Fact]
    public async Task Fetch_HttpError_IsSwallowedAndYieldsNothing()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var driver = BuildDriver(new InMemoryOAuthTokenStore(), new HttpClient(handler),
            new OuraDriverOptions { PersonalAccessToken = "pat-123" });

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
    }

    // ── Request shape, error paths ──────────────────────────────────────────────

    private static OuraDriver PatDriver(HttpClient http, CapturingLogger<OuraDriver>? logger = null) =>
        new(new InMemoryOAuthTokenStore(), http, new OuraDriverOptions { PersonalAccessToken = "pat-123" },
            logger ?? (ILogger<OuraDriver>)NullLogger<OuraDriver>.Instance);

    private static bool IsReadiness(HttpRequestMessage req) =>
        req.RequestUri!.AbsolutePath.EndsWith("daily_readiness", StringComparison.Ordinal);

    [Fact]
    public async Task Fetch_FollowsNextTokenByAppendingItToTheFirstUrl_WithTheBearerToken()
    {
        var readinessQueries = new List<string>();
        var auth = new List<string?>();
        var handler = new StubHttpMessageHandler(req =>
        {
            auth.Add(req.Headers.Authorization?.ToString());
            if (!IsReadiness(req))
                return StubHttpMessageHandler.Json("""{"data":[]}""");
            readinessQueries.Add(req.RequestUri!.Query);
            var next = readinessQueries.Count < 3 ? $"cursor {readinessQueries.Count}" : null;
            return StubHttpMessageHandler.Json(
                JsonSerializer.Serialize(new OuraDailyReadinessList([SampleReadiness()], next)));
        });

        var events = await DriverTestHelpers.CollectAsync(
            PatDriver(new HttpClient(handler)).FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-7)));

        Assert.Equal(3, readinessQueries.Count);
        Assert.StartsWith("?start_date=", readinessQueries[0], StringComparison.Ordinal);
        // Each cursor is appended to the first URL, not to the previous page's URL.
        Assert.Equal(readinessQueries[0] + "&next_token=cursor%201", readinessQueries[1]);
        Assert.Equal(readinessQueries[0] + "&next_token=cursor%202", readinessQueries[2]);
        Assert.Equal(3, events.Count(e => e.Type == BiometricType.ReadinessScore));
        Assert.Equal(7, auth.Count);
        Assert.All(auth, a => Assert.Equal("Bearer pat-123", a));
    }

    [Fact]
    public async Task Fetch_WhenOneEndpointFails_LogsTheUrlAndFetchesTheRest()
    {
        var handler = new StubHttpMessageHandler(req => IsReadiness(req)
            ? StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError)
            : StubHttpMessageHandler.Json("""{"data":[]}"""));
        var logger = new CapturingLogger<OuraDriver>();

        var events = await DriverTestHelpers.CollectAsync(
            PatDriver(new HttpClient(handler), logger).FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
        Assert.Equal(5, handler.RequestPaths.Count);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("Oura: API call failed for {Url}", error.Template);
        Assert.Contains("/daily_readiness?start_date=", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_MalformedPage_Throws()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("not json"));

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            DriverTestHelpers.CollectAsync(
                PatDriver(new HttpClient(handler)).FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1))));
    }

    [Fact]
    public async Task ImportFile_NameMatchingTwoRoutes_UsesTheFirst()
    {
        var json = JsonSerializer.Serialize(new OuraDailyReadinessList([SampleReadiness()], null));
        var path = _temp.WriteFile("sleep_readiness.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.ReadinessScore);
    }

    [Fact]
    public async Task FetchAndImport_MatchPropertyNamesIgnoringCase()
    {
        var json = DriverTestHelpers.UpperCaseKeys(
            JsonSerializer.Serialize(new OuraDailyReadinessList([SampleReadiness()], null)));
        var path = _temp.WriteFile("readiness.json", json);
        var handler = new StubHttpMessageHandler(req => IsReadiness(req)
            ? StubHttpMessageHandler.Json(json)
            : StubHttpMessageHandler.Json("""{"DATA":[]}"""));

        var imported = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));
        var fetched = await DriverTestHelpers.CollectAsync(
            PatDriver(new HttpClient(handler)).FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Contains(imported, e => e.Type == BiometricType.ReadinessScore);
        Assert.Contains(fetched, e => e.Type == BiometricType.ReadinessScore);
    }

    [Fact]
    public async Task ImportFile_SpO2DayWithoutAReading_IsSkipped()
    {
        var json = JsonSerializer.Serialize(new OuraSpO2List(
            [new OuraSpO2Daily("spo2-0", "2026-01-14", null), SampleSpO2()], null));
        var path = _temp.WriteFile("spo2.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Single(events);
    }

    [Fact]
    public async Task ImportFile_UnrecognisedName_LogsAWarning()
    {
        var path = _temp.WriteFile("Mystery.json", "{}");
        var logger = new CapturingLogger<OuraDriver>();
        var driver = PatDriver(new HttpClient(StubHttpMessageHandler.ThrowingOnUse()), logger);

        await DriverTestHelpers.CollectAsync(driver.ImportFileAsync(path));

        Assert.Equal(
            [
                (LogLevel.Information, "Oura: Importing file {File}", "Oura: Importing file Mystery.json"),
                (LogLevel.Warning, "Oura: Unrecognised export file name '{File}' — skipping.",
                    "Oura: Unrecognised export file name 'mystery' — skipping."),
            ],
            logger.Entries);
    }

    [Fact]
    public async Task ImportFile_MalformedFile_Throws()
    {
        var path = _temp.WriteFile("heartrate.json", "not json");

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path)));
    }
}
