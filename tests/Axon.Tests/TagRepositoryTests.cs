using Axon.Core.Domain;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;

namespace Axon.Tests;

/// <summary>
/// Integration tests for TagRepository against a real encrypted SQLite vault
/// (created + migrated via AxonDbContextFactory). Verifies the AddTagging
/// migration applies and the tag/annotation round-trip works.
/// </summary>
public sealed class TagRepositoryTests : IDisposable
{
    private readonly string _dir;

    public TagRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "axon-tagrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<AxonDbContext> NewContextAsync() =>
        await new AxonDbContextFactory(new MockHardwareVault()).CreateAsync(_dir);

    [Fact]
    public async Task AddTag_ThenGetTags_RoundTrips()
    {
        await using var db = await NewContextAsync();
        var repo = new TagRepository(db);

        await repo.AddTagAsync(new Tag(Guid.NewGuid(), "caffeine", "stimulant"));

        var tags = await repo.GetTagsAsync();
        Assert.Contains(tags, t => t.Name == "caffeine" && t.Category == "stimulant");
    }

    [Fact]
    public async Task AddAnnotation_ThenQueryRange_ReturnsOnlyInRange()
    {
        await using var db = await NewContextAsync();
        var repo = new TagRepository(db);

        var tagId = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);
        await repo.AddAnnotationAsync(new TagAnnotation(tagId, ts, 2.0, "double espresso"));

        var inRange = await repo.GetAnnotationsAsync(ts.AddHours(-1), ts.AddHours(1));
        Assert.Single(inRange);
        Assert.Equal(tagId, inRange[0].TagId);
        Assert.Equal(2.0, inRange[0].Value);
        Assert.Equal("double espresso", inRange[0].Note);

        var outOfRange = await repo.GetAnnotationsAsync(ts.AddDays(1), ts.AddDays(2));
        Assert.Empty(outOfRange);
    }
}
