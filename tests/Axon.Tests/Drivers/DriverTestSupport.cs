using System.Net;
using System.Text;
using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.Tests.Drivers;

/// <summary>
/// Fake <see cref="HttpMessageHandler"/> that records every outbound request and
/// returns a caller-supplied response. No socket is ever opened, so driver HTTP
/// paths can be exercised deterministically and offline (air-gap safe).
/// </summary>
internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

    /// <summary>Absolute paths of every request, in call order.</summary>
    public List<string> RequestPaths { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestPaths.Add(request.RequestUri!.AbsolutePath);
        return Task.FromResult(_responder(request));
    }

    /// <summary>Builds a 200 OK JSON response.</summary>
    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>Builds a bare status-only response (e.g. 500) with no body.</summary>
    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    /// <summary>Handler that fails the test if any request is made (asserts "no network").</summary>
    public static StubHttpMessageHandler ThrowingOnUse() =>
        new(_ => throw new InvalidOperationException(
            "Network access attempted during an offline-only code path."));
}

/// <summary>
/// In-memory <see cref="IOAuthTokenStore"/> for driver tests. Keyed by driverId,
/// matching the production contract.
/// </summary>
internal sealed class InMemoryOAuthTokenStore : IOAuthTokenStore
{
    private readonly Dictionary<string, OAuthTokenSet> _tokens = new(StringComparer.Ordinal);

    public InMemoryOAuthTokenStore(string? driverId = null, OAuthTokenSet? token = null)
    {
        if (driverId is not null && token is not null)
            _tokens[driverId] = token;
    }

    /// <summary>Number of <see cref="SaveTokenAsync"/> calls (asserts refresh persistence).</summary>
    public int SaveCount { get; private set; }

    public ValueTask<OAuthTokenSet?> GetTokenAsync(string driverId, CancellationToken ct = default)
        => ValueTask.FromResult(_tokens.TryGetValue(driverId, out var t) ? t : null);

    public ValueTask SaveTokenAsync(string driverId, OAuthTokenSet token, CancellationToken ct = default)
    {
        _tokens[driverId] = token;
        SaveCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RevokeTokenAsync(string driverId, CancellationToken ct = default)
    {
        _tokens.Remove(driverId);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Shared helpers for the driver test suites.</summary>
internal static class DriverTestHelpers
{
    /// <summary>A non-expired token usable for any driver's happy path.</summary>
    public static OAuthTokenSet ValidToken(string access = "access-token") =>
        new(access, "refresh-token", DateTimeOffset.UtcNow.AddHours(1));

    /// <summary>Materialises an async event stream into a list.</summary>
    public static async Task<List<BiometricEvent>> CollectAsync(
        IAsyncEnumerable<BiometricEvent> source)
    {
        var list = new List<BiometricEvent>();
        await foreach (var e in source)
            list.Add(e);
        return list;
    }
}

/// <summary>
/// Disposable temp directory for file-import tests. The driver's
/// <c>ImportFileAsync</c> routes on the file name, so tests need real files
/// with controlled names.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "axon-driver-tests", Guid.NewGuid().ToString("N"));

    public TempWorkspace() => Directory.CreateDirectory(_root);

    /// <summary>Writes <paramref name="content"/> to <paramref name="fileName"/> and returns the full path.</summary>
    public string WriteFile(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup; leaked temp files are harmless */ }
    }
}
