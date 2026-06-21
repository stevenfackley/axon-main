using Axon.Core.Ports;
using Axon.Infrastructure.Import;

namespace Axon.UI.Application;

/// <summary>
/// Imports a local CSV file into the encrypted biometric store. This is the free-tier
/// funnel-top and the offline fallback when live wearable sync is unavailable.
/// </summary>
public sealed class DataImportCoordinator(IBiometricRepository repository)
{
    /// <summary>
    /// Parses <paramref name="filePath"/> as Axon CSV and persists the events.
    /// Returns the number of events imported.
    /// </summary>
    public async Task<int> ImportCsvAsync(string filePath, CancellationToken ct = default)
    {
        var csv = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        var events = CsvBiometricImporter.Parse(csv, DateTimeOffset.UtcNow);
        if (events.Count == 0) return 0;

        await repository.IngestBatchAsync(events, ct).ConfigureAwait(false);
        return events.Count;
    }
}
