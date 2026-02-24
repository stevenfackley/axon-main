using System.Text.Json.Serialization;
using Axon.Core.Domain;

namespace Axon.Core.Serialization;

/// <summary>
/// AOT-safe JSON serialization context for all types that cross the serialization
/// boundary (Outbox payloads, gRPC DTOs, import/export files).
///
/// Uses <c>System.Text.Json</c> source generation — zero runtime reflection.
/// Add new types here as the schema evolves; never use <c>JsonSerializer</c>
/// directly with dynamic type arguments.
/// </summary>
[JsonSerializable(typeof(BiometricEvent))]
[JsonSerializable(typeof(SourceMetadata))]
[JsonSerializable(typeof(SyncOutboxEntry))]
[JsonSerializable(typeof(AuditLogEntry))]
[JsonSerializable(typeof(List<BiometricEvent>))]
[JsonSerializable(typeof(List<SyncOutboxEntry>))]
[JsonSerializable(typeof(BiometricType))]
[JsonSerializable(typeof(AuditOperation))]
[JsonSourceGenerationOptions(
    WriteIndented          = false,
    PropertyNamingPolicy   = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode         = JsonSourceGenerationMode.Serialization | JsonSourceGenerationMode.Metadata)]
public partial class AxonJsonContext : JsonSerializerContext { }
