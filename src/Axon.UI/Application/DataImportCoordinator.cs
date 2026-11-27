using Axon.Core.Ports;
using Axon.Infrastructure.Import;
using Axon.Infrastructure.Persistence;

namespace Axon.UI.Application;

/// <summary>
/// Imports a local CSV file into the encrypted biometric store. This is the free-tier
/// funnel-top and the offline fallback when live wearable sync is unavailable.
/// </summary>
/// <param name="activity">When set, each import is tracked so the data erase can stop it.</param>
public sealed class DataImportCoordinator(IBiometricRepository repository, VaultActivity? activity = null)
{
    /// <summary>
    /// Parses <paramref name="filePath"/> as Axon CSV and persists the events.
    /// Returns the number of events imported.
    /// </summary>
    public async Task<int> ImportCsvAsync(string filePath, CancellationToken ct = default)
    {
        using var operation = activity?.TryBegin("a CSV import", ct);
        if (activity is not null && operation is null)
            throw new InvalidOperationException("Axon's data is being erased, so nothing was imported.");
        ct = operation?.Token ?? ct;

        var csv = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var events = CsvBiometricImporter.Parse(csv, DateTimeOffset.UtcNow);
        if (events.Count == 0) return 0;

        await repository.IngestBatchAsync(events, ct).ConfigureAwait(false);
        return events.Count;
    }
}
