using System.Net;
using System.Text.Json;
using Axon.Core.Domain;
using Axon.Infrastructure.Drivers.Whoop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Axon.Tests.Drivers;

/// <summary>
/// Adapter-level coverage for <see cref="WhoopDriver"/> — the wiring around the
/// already-DoD-covered <see cref="WhoopNormalizationMapper"/>: offline file import
/// (routing + deserialisation), the OAuth-gated HTTP fetch path, pagination,
/// error/empty handling, and availability/authorisation guards.
///
/// All HTTP is faked via <see cref="StubHttpMessageHandler"/>; no socket is opened.
/// </summary>
public sealed class WhoopDriverTests : IDisposable
{
    private const string DriverId = "whoop";
    private readonly TempWorkspace _temp = new();

    public void Dispose() => _temp.Dispose();

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static WhoopRecovery SampleRecovery() =>
        new(1, 100, 200, 42, "2026-01-15T07:00:00.000Z", "2026-01-15T07:00:00.000Z", "SCORED",
            new WhoopRecoveryScore(false, 75f, 52f, 68f, 97f, 36.5f));

    private static WhoopSleep SampleSleep() =>
        new(1, 42, "2026-01-15T00:30:00.000Z", "2026-01-15T07:30:00.000Z",
            "2026-01-15T00:30:00.000Z", "2026-01-15T07:30:00.000Z", false, "SCORED",
            new WhoopSleepScore(
                new WhoopSleepStageSummary(30_000_000, 3_600_000, 0, 7_200_000, 3_600_000, 5_400_000, 3, 2),
                null, 15.2f, null, null, 88f));

    private static WhoopCycle SampleCycle() =>
        new(1, 42, "2026-01-15T06:00:00.000Z", "2026-01-15T06:00:00.000Z",
            "2026-01-15T06:00:00.000Z", null, "SCORED",
            new WhoopCycleScore(14.2f, 1500f, 95, 165));

    private static WhoopWorkout SampleWorkout() =>
        new(1, 42, "2026-01-15T09:00:00.000Z", "2026-01-15T09:00:00.000Z",
            "2026-01-15T09:00:00.000Z", "2026-01-15T10:00:00.000Z", 0, "SCORED",
            new WhoopWorkoutScore(10.5f, 145, 180, 2100f, 100f, null, null, null, null));

    private static WhoopDriver BuildDriver(InMemoryOAuthTokenStore store, HttpClient http) =>
        BuildDriver(store, http, new WhoopDriverOptions { ClientId = "cid", ClientSecret = "secret" });

    private static WhoopDriver BuildDriver(
        InMemoryOAuthTokenStore store, HttpClient http, WhoopDriverOptions options)
    {
        var auth = new WhoopAuthenticator(options, http, store, NullLogger<WhoopAuthenticator>.Instance);
        return new WhoopDriver(store, http, options, auth, NullLogger<WhoopDriver>.Instance);
    }

    /// <summary>A driver whose HTTP client throws if used — for offline import tests.</summary>
    private static WhoopDriver OfflineDriver() =>
        BuildDriver(new InMemoryOAuthTokenStore(), new HttpClient(StubHttpMessageHandler.ThrowingOnUse()));

    // ── Identity ────────────────────────────────────────────────────────────────

    [Fact]
    public void Identity_MatchesWhoop()
    {
        var driver = OfflineDriver();
        Assert.Equal(DriverId, driver.DriverId);
        Assert.Equal("Whoop 4.0", driver.DisplayName);
        Assert.False(driver.SupportsOffline); // API mode requires network
    }

    // ── ImportFileAsync (offline) ───────────────────────────────────────────────

    [Fact]
    public async Task ImportFile_Recovery_EmitsEventsWithVendorAndCorrelation()
    {
        var json = JsonSerializer.Serialize(new WhoopRecoveryList([SampleRecovery()], null));
        var path = _temp.WriteFile("recovery.json", json);

        var events = await DriverTestHelpers.CollectAsync(
            OfflineDriver().ImportFileAsync(path, correlationId: "imp-1"));

        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.Type == BiometricType.RecoveryScore);
        Assert.All(events, e => Assert.Equal("Whoop", e.Source.Vendor));
        Assert.All(events, e => Assert.Equal("imp-1", e.CorrelationId));
    }

    [Fact]
    public async Task ImportFile_Sleep_EmitsSleepEvents()
    {
        var json = JsonSerializer.Serialize(new WhoopSleepList([SampleSleep()], null));
        var path = _temp.WriteFile("sleep.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.SleepDuration);
    }

    [Fact]
    public async Task ImportFile_Cycle_EmitsStrainEvents()
    {
        var json = JsonSerializer.Serialize(new WhoopCycleList([SampleCycle()], null));
        var path = _temp.WriteFile("cycle.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.StrainScore);
    }

    [Fact]
    public async Task ImportFile_Workout_EmitsTrainingLoadEvents()
    {
        var json = JsonSerializer.Serialize(new WhoopWorkoutList([SampleWorkout()], null));
        var path = _temp.WriteFile("workout.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.TrainingLoad);
    }

    [Fact]
    public async Task ImportFile_UnrecognisedName_YieldsNothing()
    {
        var json = JsonSerializer.Serialize(new WhoopRecoveryList([SampleRecovery()], null));
        var path = _temp.WriteFile("mystery.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Empty(events);
    }

    [Fact]
    public async Task ImportFile_EmptyRecordSet_YieldsNothing()
    {
        var json = JsonSerializer.Serialize(new WhoopRecoveryList([], null));
        var path = _temp.WriteFile("recovery.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Empty(events);
    }

    // ── IsAvailableAsync / AuthoriseAsync ───────────────────────────────────────

    [Fact]
    public async Task IsAvailable_TrueWhenClientIdConfigured()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()));
        Assert.True(await driver.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_FalseWhenClientIdMissing()
    {
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()),
            new WhoopDriverOptions { ClientId = "" });
        Assert.False(await driver.IsAvailableAsync());
    }

    [Fact]
    public async Task Authorise_ThrowsWhenNoToken()
    {
        var driver = OfflineDriver();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await driver.AuthoriseAsync());
    }

    [Fact]
    public async Task Authorise_SucceedsWithValidToken()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var driver = BuildDriver(store, new HttpClient(StubHttpMessageHandler.ThrowingOnUse()));

        await driver.AuthoriseAsync(); // must not throw
    }

    // ── FetchSinceAsync (HTTP) ──────────────────────────────────────────────────

    private static HttpResponseMessage WhoopHappyResponder(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("recovery", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(
                JsonSerializer.Serialize(new WhoopRecoveryList([SampleRecovery()], null)));
        // Other endpoints (sleep/cycle/workout) return a well-formed empty page.
        return StubHttpMessageHandler.Json("""{"records":[]}""");
    }

    [Fact]
    public async Task Fetch_WithValidToken_EmitsMappedEventsFromApi()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(WhoopHappyResponder);
        var driver = BuildDriver(store, new HttpClient(handler));

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-7)));

        Assert.Contains(events, e => e.Type == BiometricType.RecoveryScore);
        Assert.All(events, e => Assert.Equal("Whoop", e.Source.Vendor));
        // All four record endpoints are queried.
        Assert.Equal(4, handler.RequestPaths.Count);
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
            StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var driver = BuildDriver(store, new HttpClient(handler));

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
    }

    [Fact]
    public async Task Fetch_FollowsPaginationCursor()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var recoveryCalls = 0;

        var handler = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("recovery", StringComparison.Ordinal))
            {
                recoveryCalls++;
                // First page carries a cursor; second page terminates it.
                var nextToken = recoveryCalls == 1 ? "page-2" : null;
                return StubHttpMessageHandler.Json(
                    JsonSerializer.Serialize(new WhoopRecoveryList([SampleRecovery()], nextToken)));
            }
            return StubHttpMessageHandler.Json("""{"records":[]}""");
        });
        var driver = BuildDriver(store, new HttpClient(handler));

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Equal(2, recoveryCalls); // cursor was followed to the second page
        Assert.Equal(2, events.Count(e => e.Type == BiometricType.RecoveryScore));
        Assert.Contains(handler.RequestPaths, p => p.EndsWith("recovery", StringComparison.Ordinal));
    }

    // ── Request shape, error and cancellation paths ─────────────────────────────

    private static WhoopDriver BuildDriver(
        InMemoryOAuthTokenStore store, HttpClient http, CapturingLogger<WhoopDriver> logger)
    {
        var options = new WhoopDriverOptions { ClientId = "cid", ClientSecret = "secret" };
        var auth = new WhoopAuthenticator(options, http, store, NullLogger<WhoopAuthenticator>.Instance);
        return new WhoopDriver(store, http, options, auth, logger);
    }

    private static bool IsRecovery(HttpRequestMessage req) =>
        req.RequestUri!.AbsolutePath.EndsWith("recovery", StringComparison.Ordinal);

    [Fact]
    public async Task Fetch_SendsTheBearerTokenAndStartLimitQuery_ThenTheCursor()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var recoveryQueries = new List<string>();
        var auth = new List<string?>();
        var handler = new StubHttpMessageHandler(req =>
        {
            auth.Add(req.Headers.Authorization?.ToString());
            if (!IsRecovery(req))
                return StubHttpMessageHandler.Json("""{"records":[]}""");
            recoveryQueries.Add(req.RequestUri!.Query);
            var next = recoveryQueries.Count == 1 ? "page 2" : null;
            return StubHttpMessageHandler.Json(
                JsonSerializer.Serialize(new WhoopRecoveryList([SampleRecovery()], next)));
        });
        var driver = BuildDriver(store, new HttpClient(handler));
        var since = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        await DriverTestHelpers.CollectAsync(driver.FetchSinceAsync(since));

        const string first = "?start=2026-01-10T00%3A00%3A00.0000000%2B00%3A00&limit=25";
        Assert.Equal([first, first + "&nextToken=page%202"], recoveryQueries);
        Assert.Equal(5, auth.Count);
        Assert.All(auth, a => Assert.Equal("Bearer access-token", a));
    }

    [Fact]
    public async Task Fetch_WhenOneEndpointFails_LogsItRaisesFetchIncompleteAndFetchesTheRest()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(req => IsRecovery(req)
            ? StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError)
            : StubHttpMessageHandler.Json("""{"records":[]}"""));
        var logger = new CapturingLogger<WhoopDriver>();
        var driver = BuildDriver(store, new HttpClient(handler), logger);
        int incomplete = 0;
        driver.FetchIncomplete += (_, _) => incomplete++;

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
        Assert.Equal(4, handler.RequestPaths.Count);
        Assert.Equal(1, incomplete);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("Whoop: API call failed for {Endpoint}", error.Template);
        Assert.EndsWith("/developer/v1/recovery", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_CancelledDuringASend_SwallowsThatSendThenThrowsBeforeTheNextEndpoint()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var driver = BuildDriver(store, new HttpClient(handler));
        int incomplete = 0;
        driver.FetchIncomplete += (_, _) => incomplete++;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DriverTestHelpers.CollectAsync(driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1), cts.Token)));

        Assert.Single(handler.RequestPaths);
        Assert.Equal(1, incomplete);
    }

    [Fact]
    public async Task Fetch_MalformedPage_Throws()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("not json"));
        var driver = BuildDriver(store, new HttpClient(handler));

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            DriverTestHelpers.CollectAsync(driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1))));
    }

    [Fact]
    public async Task Fetch_NullPage_EndsThatEndpointWithoutFlaggingIt()
    {
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(req => IsRecovery(req)
            ? StubHttpMessageHandler.Json("null")
            : StubHttpMessageHandler.Json("""{"records":[]}"""));
        var driver = BuildDriver(store, new HttpClient(handler));
        int incomplete = 0;
        driver.FetchIncomplete += (_, _) => incomplete++;

        var events = await DriverTestHelpers.CollectAsync(
            driver.FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Empty(events);
        Assert.Equal(4, handler.RequestPaths.Count);
        Assert.Equal(0, incomplete);
    }

    [Fact]
    public async Task ImportFile_NameMatchingTwoRoutes_UsesTheFirstAndIgnoresCase()
    {
        var json = JsonSerializer.Serialize(new WhoopRecoveryList([SampleRecovery()], null));
        var path = _temp.WriteFile("Sleep-RECOVERY.json", json);

        var events = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));

        Assert.Contains(events, e => e.Type == BiometricType.RecoveryScore);
    }

    [Fact]
    public async Task FetchAndImport_MatchPropertyNamesIgnoringCase()
    {
        var json = DriverTestHelpers.UpperCaseKeys(
            JsonSerializer.Serialize(new WhoopRecoveryList([SampleRecovery()], null)));
        var path = _temp.WriteFile("recovery.json", json);
        var store = new InMemoryOAuthTokenStore(DriverId, DriverTestHelpers.ValidToken());
        var handler = new StubHttpMessageHandler(req => IsRecovery(req)
            ? StubHttpMessageHandler.Json(json)
            : StubHttpMessageHandler.Json("""{"RECORDS":[]}"""));

        var imported = await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path));
        var fetched = await DriverTestHelpers.CollectAsync(
            BuildDriver(store, new HttpClient(handler)).FetchSinceAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Contains(imported, e => e.Type == BiometricType.RecoveryScore);
        Assert.Contains(fetched, e => e.Type == BiometricType.RecoveryScore);
    }

    [Fact]
    public async Task ImportFile_UnrecognisedName_LogsAWarning()
    {
        var path = _temp.WriteFile("Mystery.json", "{}");
        var logger = new CapturingLogger<WhoopDriver>();
        var driver = BuildDriver(new InMemoryOAuthTokenStore(),
            new HttpClient(StubHttpMessageHandler.ThrowingOnUse()), logger);

        await DriverTestHelpers.CollectAsync(driver.ImportFileAsync(path));

        Assert.Equal(
            [
                (LogLevel.Information, "Whoop: Importing file {File}", "Whoop: Importing file Mystery.json"),
                (LogLevel.Warning, "Whoop: Unrecognised export file name '{File}' — skipping.",
                    "Whoop: Unrecognised export file name 'mystery' — skipping."),
            ],
            logger.Entries);
    }

    [Fact]
    public async Task ImportFile_MalformedFile_Throws()
    {
        var path = _temp.WriteFile("recovery.json", "not json");

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path)));
    }

    [Fact]
    public async Task ImportFile_NullDocument_YieldsNothing()
    {
        var path = _temp.WriteFile("recovery.json", "null");

        Assert.Empty(await DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path)));
    }

    [Fact]
    public async Task ImportFile_MissingFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "axon-driver-tests", Guid.NewGuid().ToString("N"), "recovery.json");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            DriverTestHelpers.CollectAsync(OfflineDriver().ImportFileAsync(path)));
    }
}
