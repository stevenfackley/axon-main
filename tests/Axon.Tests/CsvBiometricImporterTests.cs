using Axon.Core.Domain;
using Axon.Infrastructure.Import;

namespace Axon.Tests;

/// <summary>
/// Tests the lenient Axon CSV importer: header mapping, type/value parsing,
/// skipping of malformed rows, and deterministic (idempotent) event Ids.
/// </summary>
public class CsvBiometricImporterTests
{
    private static readonly DateTimeOffset Ingested =
        new(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_ValidRows_ProducesEvents()
    {
        const string csv = """
            timestamp,type,value,unit
            2026-06-20T07:30:00Z,HeartRate,58,bpm
            2026-06-20T07:31:00Z,HeartRateVariability,72,ms
            """;

        var events = CsvBiometricImporter.Parse(csv, Ingested);

        Assert.Equal(2, events.Count);
        Assert.Equal(BiometricType.HeartRate, events[0].Type);
        Assert.Equal(58, events[0].Value);
        Assert.Equal("bpm", events[0].Unit);
        Assert.Equal("CSV", events[0].Source.Vendor);
    }

    [Fact]
    public void Parse_FlexibleColumnOrder_MatchesByHeaderName()
    {
        const string csv = """
            type,value,unit,timestamp
            HeartRate,60,bpm,2026-06-20T07:30:00Z
            """;

        var events = CsvBiometricImporter.Parse(csv, Ingested);

        Assert.Single(events);
        Assert.Equal(60, events[0].Value);
        Assert.Equal(BiometricType.HeartRate, events[0].Type);
    }

    [Fact]
    public void Parse_UnknownType_RowSkipped()
    {
        const string csv = """
            timestamp,type,value,unit
            2026-06-20T07:30:00Z,NotARealType,99,x
            2026-06-20T07:31:00Z,HeartRate,60,bpm
            """;

        var events = CsvBiometricImporter.Parse(csv, Ingested);

        Assert.Single(events);
        Assert.Equal(BiometricType.HeartRate, events[0].Type);
    }

    [Fact]
    public void Parse_MalformedValueOrTimestamp_RowsSkipped()
    {
        const string csv = """
            timestamp,type,value,unit
            2026-06-20T07:30:00Z,HeartRate,notanumber,bpm
            notadate,HeartRate,60,bpm
            2026-06-20T07:32:00Z,HeartRate,61,bpm
            """;

        var events = CsvBiometricImporter.Parse(csv, Ingested);

        Assert.Single(events);
        Assert.Equal(61, events[0].Value);
    }

    [Fact]
    public void Parse_BlankLinesAndComments_Ignored()
    {
        const string csv = """
            # my export
            timestamp,type,value,unit

            2026-06-20T07:30:00Z,HeartRate,60,bpm

            """;

        var events = CsvBiometricImporter.Parse(csv, Ingested);
        Assert.Single(events);
    }

    [Fact]
    public void Parse_MissingUnitColumn_DefaultsToEmpty()
    {
        const string csv = """
            timestamp,type,value
            2026-06-20T07:30:00Z,HeartRate,60
            """;

        var events = CsvBiometricImporter.Parse(csv, Ingested);

        Assert.Single(events);
        Assert.Equal("", events[0].Unit);
    }

    [Fact]
    public void Parse_SameInput_ProducesDeterministicIds()
    {
        const string csv = """
            timestamp,type,value,unit
            2026-06-20T07:30:00Z,HeartRate,60,bpm
            """;

        var a = CsvBiometricImporter.Parse(csv, Ingested);
        var b = CsvBiometricImporter.Parse(csv, Ingested.AddHours(5)); // different ingest time

        // Id is derived from sample identity, not ingestion time → idempotent re-import.
        Assert.Equal(a[0].Id, b[0].Id);
    }

    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {
        Assert.Empty(CsvBiometricImporter.Parse("", Ingested));
        Assert.Empty(CsvBiometricImporter.Parse("timestamp,type,value,unit", Ingested));
    }
}
