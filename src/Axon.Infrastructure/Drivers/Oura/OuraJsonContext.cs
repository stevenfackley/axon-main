using System.Text.Json.Serialization;

namespace Axon.Infrastructure.Drivers.Oura;

/// <summary>
/// Source-generated (AOT-safe) JSON metadata for the Oura API pages and export files.
/// Property names match case-insensitively, as the driver has always read them.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(OuraDailyReadinessList))]
[JsonSerializable(typeof(OuraSleepSessionList))]
[JsonSerializable(typeof(OuraDailyActivityList))]
[JsonSerializable(typeof(OuraHeartRateList))]
[JsonSerializable(typeof(OuraSpO2List))]
internal sealed partial class OuraJsonContext : JsonSerializerContext { }
