using System.Text.Json.Serialization;

namespace Axon.Infrastructure.Drivers.Whoop;

/// <summary>
/// Source-generated (AOT-safe) JSON metadata for the Whoop API pages and export files.
/// Property names match case-insensitively, as the driver has always read them.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WhoopRecoveryList))]
[JsonSerializable(typeof(WhoopSleepList))]
[JsonSerializable(typeof(WhoopCycleList))]
[JsonSerializable(typeof(WhoopWorkoutList))]
internal sealed partial class WhoopJsonContext : JsonSerializerContext { }
