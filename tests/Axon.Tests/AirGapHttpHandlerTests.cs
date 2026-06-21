using System.Net;
using System.Net.Http;
using Axon.UI.Application;

namespace Axon.Tests;

/// <summary>
/// Verifies that Air-Gap Mode actually blocks outbound HTTP at the handler level,
/// and that disabling it restores normal pass-through.
/// </summary>
public class AirGapHttpHandlerTests
{
    private static HttpClient BuildClient(AirGapState state, out CountingHandler inner)
    {
        inner = new CountingHandler();
        var handler = new AirGapHttpHandler(state) { InnerHandler = inner };
        return new HttpClient(handler);
    }

    [Fact]
    public async Task WhenAirGapEnabled_RequestIsBlocked_AndNeverReachesInnerHandler()
    {
        var state = new AirGapState { Enabled = true };
        var client = BuildClient(state, out var inner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("https://api.prod.whoop.com/developer/v1/recovery"));

        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task WhenAirGapDisabled_RequestPassesThrough()
    {
        var state = new AirGapState { Enabled = false };
        var client = BuildClient(state, out var inner);

        var response = await client.GetAsync("https://example.com/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task TogglingStateAtRuntime_ChangesEnforcement()
    {
        var state = new AirGapState { Enabled = false };
        var client = BuildClient(state, out var inner);

        await client.GetAsync("https://example.com/");   // allowed
        state.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("https://example.com/")); // now blocked

        Assert.Equal(1, inner.CallCount);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
