namespace Axon.Core.Domain;

/// <summary>
/// Records a single application of a <see cref="Tag"/> at a specific moment in time.
/// </summary>
/// <param name="TagId">Foreign key to <see cref="Tag.Id"/>.</param>
/// <param name="Timestamp">UTC instant the tag was applied.</param>
/// <param name="Value">
///     Optional quantitative value (e.g. dose in mg for a supplement).
///     <c>null</c> when the annotation is purely qualitative.
/// </param>
/// <param name="Note">Optional free-text note attached to this annotation.</param>
public sealed record TagAnnotation(
    Guid TagId,
    DateTimeOffset Timestamp,
    double? Value,
    string? Note);
