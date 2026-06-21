using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence.Mappers;
using Microsoft.EntityFrameworkCore;

namespace Axon.Infrastructure.Persistence;

/// <summary><see cref="ITagRepository"/> backed by the encrypted <see cref="AxonDbContext"/>.</summary>
public sealed class TagRepository(AxonDbContext db) : ITagRepository
{
    public async ValueTask<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken ct = default)
    {
        var rows = await db.Tags.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var result = new Tag[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            result[i] = TagMapper.ToDomain(rows[i]);
        return result;
    }

    public async ValueTask AddTagAsync(Tag tag, CancellationToken ct = default)
    {
        db.Tags.Add(TagMapper.ToEntity(tag));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask AddAnnotationAsync(TagAnnotation annotation, CancellationToken ct = default)
    {
        db.TagAnnotations.Add(TagMapper.ToEntity(annotation));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<TagAnnotation>> GetAnnotationsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        long f = from.ToUnixTimeMilliseconds();
        long t = to.ToUnixTimeMilliseconds();

        var rows = await db.TagAnnotations
            .AsNoTracking()
            .Where(x => x.TimestampUnixMs >= f && x.TimestampUnixMs <= t)
            .OrderBy(x => x.TimestampUnixMs)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new TagAnnotation[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            result[i] = TagMapper.ToDomain(rows[i]);
        return result;
    }
}
