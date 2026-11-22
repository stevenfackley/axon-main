using System.Net;
using Axon.Core.Ports;
using Axon.Infrastructure.Drivers.Whoop;
using Axon.Tests.Drivers;
using Axon.UI.Application;
using Microsoft.Extensions.Logging.Abstractions;

namespace Axon.Tests;

/// <summary>
/// Covers the last-sync watermark in <see cref="WhoopSyncCoordinator.SyncNowAsync"/>.
/// The driver logs and skips failed API calls instead of throwing, so the coordinator
/// must not treat a skipped fetch as a completed sync.
/// </summary>
public sealed class WhoopSyncCoordinatorTests
{
    private static (WhoopSyncCoordinator Coordinator, RecordingOrchestrator Orchestrator) Build(
        InMemoryOAuthTokenStore store, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var options = new WhoopDriverOptions { ClientId = "cid", ClientSecret = "secret" };
        var http = new HttpClient(new StubHttpMessageHandler(responder));
        var auth = new WhoopAuthenticator(options, http, store, NullLogger<WhoopAuthenticator>.Instance);
        var driver = new WhoopDriver(store, http, options, auth, NullLogger<WhoopDriver>.Instance);
        var orchestrator = new RecordingOrchestrator();
        return (new WhoopSyncCoordinator(auth, driver, orchestrator, store, isConfigured: true), orchestrator);
    }

    private static HttpResponseMessage EmptyPage(HttpRequestMessage _) =>
        StubHttpMessageHandler.Json("""{"records":[]}""");

    [Fact]
    public async Task SyncNow_WhenTheApiCallFails_ThrowsAndKeepsTheWatermark()
    {
        var store = new InMemoryOAuthTokenStore(WhoopAuthenticator.DriverId, DriverTestHelpers.ValidToken());
        var apiDown = true;
        var (coordinator, orchestrator) = Build(store, req => apiDown
            ? StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError)
            : EmptyPage(req));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SyncNowAsync());

        apiDown = false;
        await coordinator.SyncNowAsync();

        // The retry still starts from the initial look-back, not from the failed attempt.
        Assert.True(orchestrator.Since[1] < DateTimeOffset.UtcNow.AddDays(-29));
    }

    [Fact]
    public async Task SyncNow_WithoutAToken_ThrowsAndKeepsTheWatermark()
    {
        var store = new InMemoryOAuthTokenStore();
        var (coordinator, orchestrator) = Build(store, EmptyPage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SyncNowAsync());

        await store.SaveTokenAsync(WhoopAuthenticator.DriverId, DriverTestHelpers.ValidToken());
        await coordinator.SyncNowAsync();

        Assert.True(orchestrator.Since[1] < DateTimeOffset.UtcNow.AddDays(-29));
    }

    [Fact]
    public async Task SyncNow_AfterACompleteSync_AdvancesTheWatermark()
    {
        var store = new InMemoryOAuthTokenStore(WhoopAuthenticator.DriverId, DriverTestHelpers.ValidToken());
        var (coordinator, orchestrator) = Build(store, EmptyPage);

        await coordinator.SyncNowAsync();
        await coordinator.SyncNowAsync();

        Assert.True(orchestrator.Since[1] > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    /// <summary>Drains the driver like the real orchestrator and records each window start.</summary>
    private sealed class RecordingOrchestrator : IIngestionOrchestrator
    {
        public List<DateTimeOffset> Since { get; } = [];

        public async ValueTask IngestAsync(IBiometricDriver driver, DateTimeOffset since, CancellationToken ct = default)
        {
            Since.Add(since);
            await foreach (var _ in driver.FetchSinceAsync(since, ct))
            {
            }
        }
    }
}
