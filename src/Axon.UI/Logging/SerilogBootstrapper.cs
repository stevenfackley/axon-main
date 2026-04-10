// Desktop-only: not compiled on Android/iOS (no file system path or console sink needed there).
#if !ANDROID && !IOS
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Axon.UI.Logging;

/// <summary>
/// Configures and creates the application-wide Serilog logger.
/// Call <see cref="CreateLoggerFactory"/> once at startup, before building the runtime.
/// Dispose the returned factory on app exit to flush all sinks.
/// </summary>
internal static class SerilogBootstrapper
{
    /// <summary>
    /// Builds the Serilog pipeline:
    ///   • JSON-formatted console output
    ///   • Daily rolling file: {dataDirectory}/logs/axon-.log
    ///   • Suppresses noisy Microsoft / EF Core framework noise
    ///   • Enriches every event with LogContext properties (driver name, operation, etc.)
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(string dataDirectory)
    {
        var logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        var serilogLogger = new LoggerConfiguration()
            // ── Minimum levels ───────────────────────────────────────────────
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft",                LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Avalonia",                 LogEventLevel.Warning)
            // ── Enrichment ───────────────────────────────────────────────────
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .Enrich.WithProperty("Application", "Axon")
            // ── Sinks ────────────────────────────────────────────────────────
            .WriteTo.Console(
                formatter: new Serilog.Formatting.Json.JsonFormatter(renderMessage: true))
            .WriteTo.File(
                formatter: new Serilog.Formatting.Json.JsonFormatter(renderMessage: true),
                path: Path.Combine(logDirectory, "axon-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 50 * 1024 * 1024, // 50 MB per day cap
                rollOnFileSizeLimit: true)
            .CreateLogger();

        // Assign as global static so Serilog.Log.* calls also work if needed.
        Log.Logger = serilogLogger;

        return new SerilogLoggerFactory(serilogLogger, dispose: true);
    }
}
#endif
