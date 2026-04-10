using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Axon.UI.Observability;

internal interface IHealthReportWriter
{
    Task WriteAsync(global::Axon.UI.Application.RelaySnapshot snapshot, CancellationToken ct = default);
}

internal interface IObservabilityController
{
    bool TelemetryEnabled { get; }

    void SetTelemetryEnabled(bool enabled);
}

#if !ANDROID && !IOS
internal static class AxonObservability
{
    public const string ServiceName = "Axon.UI";

    public static readonly ActivitySource ActivitySource = new($"{ServiceName}.relay");
    public static readonly Meter Meter = new(ServiceName, "1.0.0");
    public static readonly Counter<long> RelayBatchCounter = Meter.CreateCounter<long>("axon.relay.batches");
    public static readonly Counter<long> RelayEventCounter = Meter.CreateCounter<long>("axon.relay.events");
    public static readonly Counter<long> RelayFailureCounter = Meter.CreateCounter<long>("axon.relay.failures");
    public static readonly Histogram<int> PendingOutboxHistogram = Meter.CreateHistogram<int>("axon.relay.pending_outbox");
}

internal sealed class AxonObservabilityRuntime : IDisposable, IObservabilityController
{
    private readonly object _gate = new();
    private readonly ResourceBuilder _resourceBuilder;
    private readonly string? _otlpEndpoint;
    private TracerProvider? _tracerProvider;
    private MeterProvider? _meterProvider;

    private AxonObservabilityRuntime(
        ResourceBuilder resourceBuilder,
        string? otlpEndpoint,
        bool telemetryEnabled,
        IHealthReportWriter healthReportWriter)
    {
        _resourceBuilder = resourceBuilder;
        _otlpEndpoint = otlpEndpoint;
        TelemetryEnabled = telemetryEnabled;
        HealthReportWriter = healthReportWriter;
        ApplyTelemetryState();
    }

    public IHealthReportWriter HealthReportWriter { get; }
    public bool TelemetryEnabled { get; private set; }

    public static AxonObservabilityRuntime Create(string dataDirectory)
    {
        var resourceBuilder = ResourceBuilder.CreateDefault().AddService(AxonObservability.ServiceName);
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var telemetryEnabled = string.Equals(
            Environment.GetEnvironmentVariable("AXON_OTEL_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        return new AxonObservabilityRuntime(
            resourceBuilder,
            otlpEndpoint,
            telemetryEnabled,
            new JsonHealthReportWriter(dataDirectory));
    }

    public void SetTelemetryEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (TelemetryEnabled == enabled)
            {
                return;
            }

            TelemetryEnabled = enabled;
            ApplyTelemetryState();
        }
    }

    public void Dispose()
    {
        DisposeProviders();
        AxonObservability.ActivitySource.Dispose();
        AxonObservability.Meter.Dispose();
    }

    private void ApplyTelemetryState()
    {
        DisposeProviders();
        if (!TelemetryEnabled)
        {
            return;
        }

        var tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(_resourceBuilder)
            .AddSource(AxonObservability.ActivitySource.Name);

        var meterBuilder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(_resourceBuilder)
            .AddMeter(AxonObservability.Meter.Name);

        if (string.IsNullOrWhiteSpace(_otlpEndpoint))
        {
            tracerBuilder.AddConsoleExporter();
            meterBuilder.AddConsoleExporter();
        }
        else
        {
            tracerBuilder.AddOtlpExporter();
            meterBuilder.AddOtlpExporter();
        }

        _tracerProvider = tracerBuilder.Build();
        _meterProvider = meterBuilder.Build();
    }

    private void DisposeProviders()
    {
        _tracerProvider?.Dispose();
        _meterProvider?.Dispose();
        _tracerProvider = null;
        _meterProvider = null;
    }
}

internal sealed class JsonHealthReportWriter : IHealthReportWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _reportPath;

    public JsonHealthReportWriter(string dataDirectory)
    {
        var reportDirectory = Path.Combine(dataDirectory, "health");
        Directory.CreateDirectory(reportDirectory);
        _reportPath = Path.Combine(reportDirectory, "status.json");
    }

    public async Task WriteAsync(global::Axon.UI.Application.RelaySnapshot snapshot, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                status = snapshot.State is global::Axon.UI.Application.RelayState.Error ? "degraded" : "ok",
                service = AxonObservability.ServiceName,
                updatedAt = DateTimeOffset.UtcNow,
                relay = new
                {
                    state = snapshot.State.ToString(),
                    snapshot.PendingCount,
                    snapshot.LastSuccessfulSync,
                    snapshot.LastError,
                    snapshot.AirGapEnabled,
                    snapshot.TransportName
                }
            };

            var json = JsonSerializer.Serialize(payload, SerializerOptions);
            await File.WriteAllTextAsync(_reportPath, json, ct).ConfigureAwait(false);
        }
        catch
        {
            // Health report emission should never take down the desktop runtime.
        }
    }
}
#endif

internal sealed class NullHealthReportWriter : IHealthReportWriter
{
    public static readonly NullHealthReportWriter Instance = new();

    private NullHealthReportWriter()
    {
    }

    public Task WriteAsync(global::Axon.UI.Application.RelaySnapshot snapshot, CancellationToken ct = default) =>
        Task.CompletedTask;
}

internal sealed class NullObservabilityRuntime : IDisposable, IObservabilityController
{
    public static readonly NullObservabilityRuntime Instance = new();

    private NullObservabilityRuntime()
    {
    }

    public void Dispose()
    {
    }

    public bool TelemetryEnabled => false;

    public void SetTelemetryEnabled(bool enabled)
    {
    }
}
