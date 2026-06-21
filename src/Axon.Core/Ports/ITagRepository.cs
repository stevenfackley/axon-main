using Axon.Core.Domain;

namespace Axon.Core.Ports;

/// <summary>
/// Port for persisting user-defined tags and their dated annotations, used by the
/// correlation lab to associate lifestyle events with biometric outcomes.
/// </summary>
public interface ITagRepository
{
    /// <summary>Returns all defined tags.</summary>
    ValueTask<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken ct = default);

    /// <summary>Creates a new tag definition.</summary>
    ValueTask AddTagAsync(Tag tag, CancellationToken ct = default);

    /// <summary>Records a dated occurrence of a tag.</summary>
    ValueTask AddAnnotationAsync(TagAnnotation annotation, CancellationToken ct = default);

    /// <summary>Returns tag annotations whose timestamp falls within the range (inclusive).</summary>
    ValueTask<IReadOnlyList<TagAnnotation>> GetAnnotationsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
