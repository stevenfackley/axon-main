using System.Text.Json;
using Axon.UI.Application;
using Axon.UI.Observability;

namespace Axon.Tests;

public class JsonHealthReportWriterTests
{
    [Fact]
    public async Task WriteAsync_WritesOkPayloadForHealthyRelay()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "axon-tests", Guid.NewGuid().ToString("N"));
        var writer = new JsonHealthReportWriter(tempDirectory);
        var snapshot = new RelaySnapshot(
            RelayState.Idle,
            PendingCount: 2,
            LastSuccessfulSync: DateTimeOffset.UtcNow,
            LastError: null,
            AirGapEnabled: false,
            TransportName: "loopback");

        await writer.WriteAsync(snapshot);

        var reportPath = Path.Combine(tempDirectory, "health", "status.json");
        Assert.True(File.Exists(reportPath));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Idle", document.RootElement.GetProperty("relay").GetProperty("state").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("relay").GetProperty("PendingCount").GetInt32());
    }

    [Fact]
    public async Task WriteAsync_WritesDegradedPayloadForErroredRelay()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "axon-tests", Guid.NewGuid().ToString("N"));
        var writer = new JsonHealthReportWriter(tempDirectory);
        var snapshot = new RelaySnapshot(
            RelayState.Error,
            PendingCount: 5,
            LastSuccessfulSync: null,
            LastError: "Transport rejected batch.",
            AirGapEnabled: false,
            TransportName: "loopback");

        await writer.WriteAsync(snapshot);

        var reportPath = Path.Combine(tempDirectory, "health", "status.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal("degraded", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Transport rejected batch.", document.RootElement.GetProperty("relay").GetProperty("LastError").GetString());
    }
}
