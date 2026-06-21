namespace Axon.Core.Domain;

/// <summary>
/// A user-defined label attached to time-points (e.g. supplement, stimulus, lifestyle event).
/// </summary>
/// <param name="Id">Stable identity for the tag.</param>
/// <param name="Name">Human-readable label (e.g. "Caffeine", "Cold shower").</param>
/// <param name="Category">
///     High-level grouping. Recommended values: stimulant, depressant, supplement, lifestyle.
/// </param>
public sealed record Tag(Guid Id, string Name, string Category);
