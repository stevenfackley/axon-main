using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Axon.Infrastructure.Drivers;

/// <summary>
/// Reads JSON pages from a vendor REST API with a Bearer token, for the driver adapters.
/// Each driver supplies its own URL scheme, cursor and failure reporting.
/// </summary>
internal static class VendorHttpReader
{
    /// <summary>
    /// Requests the first page (<paramref name="urlForCursor"/> is called with <c>null</c> for
    /// it), then keeps requesting while <paramref name="nextCursor"/> returns a cursor. A request
    /// that cannot be sent, or that returns a non-success status, ends the read after
    /// <paramref name="onFailure"/> is called with the exception and the URL; a cancelled send
    /// counts as such a failure. A body that is not valid JSON throws, and a <c>null</c> body
    /// ends the read.
    /// </summary>
    public static async IAsyncEnumerable<TPage> GetPagesAsync<TPage>(
        HttpClient http,
        Func<string?, string> urlForCursor,
        string bearerToken,
        JsonTypeInfo<TPage> pageType,
        Func<TPage, string?> nextCursor,
        Action<Exception, string> onFailure,
        [EnumeratorCancellation] CancellationToken ct)
        where TPage : class
    {
        string? cursor = null;

        do
        {
            ct.ThrowIfCancellationRequested();

            var url = urlForCursor(cursor);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                onFailure(ex, url);
                yield break;
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            TPage? page = null;
            try
            {
                page = await JsonSerializer.DeserializeAsync(stream, pageType, ct).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (page is null) yield break;

            yield return page;

            cursor = nextCursor(page);

        } while (cursor is not null);
    }
}
