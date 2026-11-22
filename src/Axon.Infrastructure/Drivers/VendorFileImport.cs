using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Axon.Core.Domain;

namespace Axon.Infrastructure.Drivers;

/// <summary>
/// The offline import path shared by the driver adapters: a vendor export file is routed
/// on its name to one parser, which reads the whole file as one JSON document.
/// </summary>
internal static class VendorFileImport
{
    /// <summary>One kind of export file: the name fragments that select it, and its parser.</summary>
    internal sealed record Route(
        string[] NameKeys,
        Func<Stream, CancellationToken, IAsyncEnumerable<BiometricEvent>> Read);

    /// <summary>
    /// Opens <paramref name="jsonFilePath"/> and reads it with the first route whose key
    /// appears in <paramref name="fileName"/> (the lower-case name without its extension),
    /// or calls <paramref name="onUnrecognised"/> when none does. The file is opened, and
    /// must exist, either way.
    /// </summary>
    public static async IAsyncEnumerable<BiometricEvent> ReadAsync(
        string jsonFilePath,
        string fileName,
        IReadOnlyList<Route> routes,
        Action<string> onUnrecognised,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = File.OpenRead(jsonFilePath);
        try
        {
            var route = routes.FirstOrDefault(r => r.NameKeys.Any(fileName.Contains));
            if (route is null)
            {
                onUnrecognised(fileName);
            }
            else
            {
                await foreach (var evt in route.Read(stream, ct).ConfigureAwait(false))
                    yield return evt;
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads <paramref name="stream"/> as one <typeparamref name="TList"/> and yields, record by
    /// record, the events <paramref name="map"/> makes; a <c>null</c> document yields nothing.
    /// </summary>
    public static async IAsyncEnumerable<BiometricEvent> ParseAsync<TList, TRecord>(
        Stream stream,
        JsonTypeInfo<TList> listType,
        Func<TList, IEnumerable<TRecord>> records,
        Func<TRecord, IEnumerable<BiometricEvent>> map,
        [EnumeratorCancellation] CancellationToken ct)
        where TList : class
    {
        var list = await JsonSerializer.DeserializeAsync(stream, listType, ct).ConfigureAwait(false);
        if (list is null) yield break;

        foreach (var record in records(list))
            foreach (var evt in map(record))
                yield return evt;
    }
}
