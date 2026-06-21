namespace Axon.Infrastructure.Persistence.Entities;

/// <summary>
/// EF Core entity for the <c>TagAnnotations</c> table — a dated occurrence of a
/// <see cref="TagEntity"/> (optionally with a dose/value and a note), used by the
/// correlation lab to associate tagged days with biometric outcomes.
/// </summary>
internal sealed class TagAnnotationEntity
{
    public Guid Id { get; set; }
    public Guid TagId { get; set; }
    public long TimestampUnixMs { get; set; }
    public double? Value { get; set; }
    public string? Note { get; set; }
}
