namespace Axon.Core.Domain;

/// <summary>
/// Immutable provenance descriptor attached to every <see cref="BiometricEvent"/>.
/// Captures the originating device, vendor, and confidence so downstream
/// de-duplication and trust-scoring logic can act on raw provenance.
/// </summary>
/// <param name="DeviceId">Hardware serial or logical identifier of the source device.</param>
/// <param name="Vendor">Canonical vendor name (e.g. "Whoop", "Garmin", "Oura", "Apple", "Google").</param>
/// <param name="FirmwareVersion">Optional firmware string for protocol-version debugging.</param>
/// <param name="ConfidenceScore">
///     Normalised confidence in [0.0, 1.0]. Values below 0.5 are flagged as
///     unreliable and excluded from ML.NET inference by default.
/// </param>
/// <param name="IngestionTimestamp">UTC wall-clock time Axon received the raw payload.</param>
public sealed record SourceMetadata(
    string  DeviceId,
    string  Vendor,
    string? FirmwareVersion,
    float   ConfidenceScore,
    DateTimeOffset IngestionTimestamp)
{
    /// <summary>
    /// PII Shield: never emit DeviceId or Vendor in logs/ToString output.
    /// </summary>
    public override string ToString() =>
        $"SourceMetadata {{ Vendor=[REDACTED], Confidence={ConfidenceScore:F2} }}";
}
