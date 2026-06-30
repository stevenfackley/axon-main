using System.Net;
using System.Text.Json;
using Axon.Core.Domain;
using Axon.Infrastructure.Drivers.Whoop;
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
}
