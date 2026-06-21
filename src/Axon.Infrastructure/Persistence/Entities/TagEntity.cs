namespace Axon.Infrastructure.Persistence.Entities;

/// <summary>
/// EF Core entity for the <c>Tags</c> table — a user-defined lifestyle/context tag
/// (e.g. "caffeine", "alcohol", "travel") that can be correlated against biometrics.
/// </summary>
internal sealed class TagEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
