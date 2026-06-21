using Axon.Core.Domain;
using Axon.Infrastructure.Persistence.Entities;

namespace Axon.Infrastructure.Persistence.Mappers;

/// <summary>Domain ↔ entity translation for <see cref="Tag"/> / <see cref="TagAnnotation"/>.</summary>
internal static class TagMapper
{
    public static TagEntity ToEntity(Tag tag) =>
        new() { Id = tag.Id, Name = tag.Name, Category = tag.Category };

    public static Tag ToDomain(TagEntity e) => new(e.Id, e.Name, e.Category);

    public static TagAnnotationEntity ToEntity(TagAnnotation a) =>
        new()
        {
            Id = Guid.NewGuid(),
            TagId = a.TagId,
            TimestampUnixMs = a.Timestamp.ToUnixTimeMilliseconds(),
            Value = a.Value,
            Note = a.Note,
        };

    public static TagAnnotation ToDomain(TagAnnotationEntity e) =>
        new(
            e.TagId,
            DateTimeOffset.FromUnixTimeMilliseconds(e.TimestampUnixMs),
            e.Value,
            e.Note);
}
