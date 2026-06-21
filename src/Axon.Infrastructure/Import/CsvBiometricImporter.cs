using System.Globalization;
using Axon.Core.Domain;
using Axon.Infrastructure.Drivers;

namespace Axon.Infrastructure.Import;

/// <summary>
/// Imports biometric data from a simple, documented Axon CSV format so users can bring
/// data from any source (retired devices, manual logs, vendor exports) into the local vault.
///
/// Expected header (column order is flexible, matched by name, case-insensitive):
///   <c>timestamp,type,value,unit</c>
///   • <c>timestamp</c> — ISO-8601 / round-trip (e.g. 2026-06-20T07:30:00Z)
///   • <c>type</c>      — an ACS <see cref="BiometricType"/> name (e.g. HeartRate)
///   • <c>value</c>     — numeric, invariant culture
///   • <c>unit</c>      — optional unit string
///
/// Parsing is lenient: malformed or unrecognized rows are skipped rather than failing the
/// whole import. Event Ids are deterministic, so re-importing the same file is idempotent.
/// </summary>
public static class CsvBiometricImporter
{
    private const string Vendor = "CSV";
    private const string DeviceId = "csv-import";

    /// <summary>Parses CSV content into ACS events. Unparseable rows are skipped.</summary>
    public static IReadOnlyList<BiometricEvent> Parse(string csv, DateTimeOffset ingestionTime)
    {
        var lines = csv.Split('\n');
        var result = new List<BiometricEvent>();

        int[]? cols = null; // [timestamp, type, value, unit] indices; -1 if absent
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var fields = line.Split(',');
            if (cols is null)
            {
                cols = MapHeader(fields);
                continue; // first data line is the header
            }

            if (TryParseRow(fields, cols, ingestionTime, out var evt))
                result.Add(evt);
        }

        return result;
    }

    private static int[] MapHeader(string[] header)
    {
        int Find(string name)
        {
            for (int i = 0; i < header.Length; i++)
                if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
        return [Find("timestamp"), Find("type"), Find("value"), Find("unit")];
    }

    private static bool TryParseRow(
        string[] fields, int[] cols, DateTimeOffset ingestionTime, out BiometricEvent evt)
    {
        evt = default!;
        int tsIdx = cols[0], typeIdx = cols[1], valIdx = cols[2], unitIdx = cols[3];

        if (tsIdx < 0 || typeIdx < 0 || valIdx < 0) return false;
        if (tsIdx >= fields.Length || typeIdx >= fields.Length || valIdx >= fields.Length) return false;

        if (!DateTimeOffset.TryParse(
                fields[tsIdx].Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var timestamp))
            return false;

        if (!Enum.TryParse<BiometricType>(fields[typeIdx].Trim(), ignoreCase: true, out var type))
            return false;

        if (!double.TryParse(
                fields[valIdx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        var unit = unitIdx >= 0 && unitIdx < fields.Length ? fields[unitIdx].Trim() : "";

        evt = new BiometricEvent(
            Id: DriverUtilities.DeterministicId(Vendor, DeviceId, timestamp, type),
            Timestamp: timestamp,
            Type: type,
            Value: value,
            Unit: unit,
            Source: new SourceMetadata(DeviceId, Vendor, null, 0.8f, ingestionTime));
        return true;
    }
}
