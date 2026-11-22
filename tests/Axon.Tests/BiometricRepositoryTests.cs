using Axon.Core.Domain;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Axon.Tests;

/// <summary>
/// Integration tests for <see cref="BiometricRepository.IngestBatchAsync"/> against a real
/// encrypted SQLite vault (created + migrated via AxonDbContextFactory). Event ids are
/// deterministic, so a CSV re-import or a Whoop re-sync over an overlapping window sends
/// ids that are already stored.
/// </summary>
public sealed class BiometricRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Ts = new(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir;

    public BiometricRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "axon-biorepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<AxonDbContext> NewContextAsync() =>
        await new AxonDbContextFactory(new MockHardwareVault()).CreateAsync(_dir);

    private static BiometricEvent MakeEvent(Guid id, double value = 72, string unit = "bpm") =>
        new(
            Id: id,
            Timestamp: Ts,
            Type: BiometricType.HeartRate,
            Value: value,
            Unit: unit,
            Source: new SourceMetadata("dev-1", "Test", null, 0.9f, Ts));

    private static Task<IReadOnlyList<BiometricEvent>> StoredAsync(BiometricRepository repo) =>
        repo.QueryRangeAsync(BiometricType.HeartRate, Ts.AddHours(-1), Ts.AddHours(1)).AsTask();

    [Fact]
    public async Task IngestBatch_SameEventTwice_StoresItOnce()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);
        var evt = MakeEvent(Guid.NewGuid());

        await repo.IngestBatchAsync([evt]);
        await repo.IngestBatchAsync([evt]); // re-import of the same sample

        Assert.Single(await StoredAsync(repo));
        Assert.Equal(1, await db.SyncOutbox.CountAsync());
    }

    [Fact]
    public async Task IngestBatch_DuplicateIdsInOneBatch_StoresOne()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);
        var id = Guid.NewGuid();

        await repo.IngestBatchAsync([MakeEvent(id, value: 60), MakeEvent(id, value: 61)]);

        var stored = Assert.Single(await StoredAsync(repo));
        Assert.Equal(60, stored.Value);
        Assert.Equal(1, await db.SyncOutbox.CountAsync());
    }

    [Fact]
    public async Task IngestBatch_AfterAFailedSave_LaterBatchesStillSave()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);

        // Unit is NOT NULL in the schema, so this save fails inside SQLite.
        var bad = MakeEvent(Guid.NewGuid(), unit: null!);
        await Assert.ThrowsAsync<DbUpdateException>(async () => await repo.IngestBatchAsync([bad]));

        var good = MakeEvent(Guid.NewGuid());
        await repo.IngestBatchAsync([good]);

        var stored = Assert.Single(await StoredAsync(repo));
        Assert.Equal(good.Id, stored.Id);
    }
}
