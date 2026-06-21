namespace Axon.Core.Licensing;

/// <summary>
/// Discrete product features that can be enabled or disabled per <see cref="LicenseTier"/>.
/// </summary>
/// <remarks>
/// Feature mapping (PRD §6):
/// <list type="table">
///   <listheader><term>Feature</term><description>Minimum tier</description></listheader>
///   <item><term>ManualImport</term><description>Free</description></item>
///   <item><term>BasicVisualization</term><description>Free</description></item>
///   <item><term>TwelveMonthHistory</term><description>Free</description></item>
///   <item><term>ApiSync</term><description>Pro / Lifetime</description></item>
///   <item><term>UnlimitedHistory</term><description>Pro / Lifetime</description></item>
///   <item><term>MlInsightEngine</term><description>Pro / Lifetime</description></item>
///   <item><term>CorrelationLab</term><description>Pro / Lifetime</description></item>
///   <item><term>ShareableExports</term><description>Pro / Lifetime</description></item>
///   <item><term>MultiSource</term><description>Pro / Lifetime</description></item>
///   <item><term>SovereignSync</term><description>Pro / Lifetime</description></item>
/// </list>
/// </remarks>
public enum Feature
{
    // ── Free-tier features ────────────────────────────────────────────────────

    /// <summary>Manual CSV/JSON import of legacy or exported device data.</summary>
    ManualImport,

    /// <summary>Single-source basic chart visualization.</summary>
    BasicVisualization,

    /// <summary>Access to the last 12 months of stored biometric history.</summary>
    TwelveMonthHistory,

    // ── Pro / Lifetime features ───────────────────────────────────────────────

    /// <summary>Automated real-time sync with Whoop, Garmin Connect, and Oura Cloud APIs.</summary>
    ApiSync,

    /// <summary>Unbounded access to all stored biometric history beyond the 12-month window.</summary>
    UnlimitedHistory,

    /// <summary>
    /// Local ML.NET inference: anomaly detection, recovery forecasting,
    /// and readiness-score prediction.
    /// </summary>
    MlInsightEngine,

    /// <summary>
    /// Drag-and-drop correlation engine for finding statistical relationships
    /// between arbitrary biometric variables.
    /// </summary>
    CorrelationLab,

    /// <summary>Export and share annotated health reports outside the app.</summary>
    ShareableExports,

    /// <summary>Ingestion from more than one wearable/data source simultaneously.</summary>
    MultiSource,

    /// <summary>
    /// Cross-device peer-to-peer gRPC sync with zero-knowledge encryption
    /// (keys stored in hardware TPM/Secure Enclave).
    /// </summary>
    SovereignSync,
}
