using System.Net.Http;

namespace Axon.UI.Application;

/// <summary>
/// Enforces <see cref="AirGapState"/> at the HTTP boundary: when air-gap mode is
/// enabled, every outbound request is rejected before it leaves the process.
/// This is the teeth behind the "disable all outbound networking" toggle —
/// the request never reaches the socket.
/// </summary>
public sealed class AirGapHttpHandler(AirGapState state) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (state.Enabled)
            throw new InvalidOperationException(
                "Air-Gap Mode is enabled — outbound network requests are blocked.");

        return base.SendAsync(request, cancellationToken);
    }
}
