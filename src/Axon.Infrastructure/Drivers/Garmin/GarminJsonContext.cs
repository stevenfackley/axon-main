using System.Text.Json.Serialization;

namespace Axon.Infrastructure.Drivers.Garmin;

/// <summary>
/// Source-generated (AOT-safe) JSON metadata for the Garmin API responses and export files.
/// Property names match case-insensitively, as the driver has always read them.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GarminDailySummaryList))]
[JsonSerializable(typeof(GarminSleepSummaryList))]
[JsonSerializable(typeof(GarminHrvSummaryList))]
[JsonSerializable(typeof(GarminBodyCompositionList))]
internal sealed partial class GarminJsonContext : JsonSerializerContext { }
