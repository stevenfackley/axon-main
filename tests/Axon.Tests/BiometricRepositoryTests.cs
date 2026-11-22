using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Persistence.Decorators;
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

    private static BiometricEvent At(DateTimeOffset ts, BiometricType type, double value) =>
        new(
            Id: Guid.NewGuid(),
            Timestamp: ts,
            Type: type,
            Value: value,
            Unit: "u",
            Source: new SourceMetadata("dev-1", "Test", null, 0.9f, ts),
            CorrelationId: "corr-1");

    [Fact]
    public async Task QueryAndStreamRange_ReturnOneTypeInAHalfOpenRangeOldestFirst()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);
        var later = At(Ts.AddMinutes(30), BiometricType.HeartRate, 70);
        var first = At(Ts, BiometricType.HeartRate, 60);
        var atEnd = At(Ts.AddHours(1), BiometricType.HeartRate, 80); // the range end is exclusive
        var otherType = At(Ts.AddMinutes(10), BiometricType.HeartRateVariability, 50);
        await repo.IngestBatchAsync([later, first, atEnd, otherType]);

        var queried = await repo.QueryRangeAsync(BiometricType.HeartRate, Ts, Ts.AddHours(1));
        var streamed = new List<BiometricEvent>();
        await foreach (var e in repo.StreamRangeAsync(BiometricType.HeartRate, Ts, Ts.AddHours(1)))
            streamed.Add(e);

        Assert.Equal([first, later], queried);
        Assert.Equal([first, later], streamed);
    }

    [Fact]
    public async Task GetAggregates_GroupsIntoFixedWidthBuckets()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);
        await repo.IngestBatchAsync(
        [
            At(Ts, BiometricType.HeartRate, 60),
            At(Ts.AddMinutes(10), BiometricType.HeartRate, 80),
            At(Ts.AddMinutes(70), BiometricType.HeartRate, 90),
        ]);

        var buckets = await repo.GetAggregatesAsync(BiometricType.HeartRate, Ts, Ts.AddHours(2), 3600);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(new AggregateBucket(Ts, 60, 80, 70, 2), buckets[0]);
        Assert.Equal(new AggregateBucket(Ts.AddHours(1), 90, 90, 90, 1), buckets[1]);
    }

    [Fact]
    public async Task GetLatestVitals_ReturnsTheNewestEventPerType()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);
        var olderHr = At(Ts, BiometricType.HeartRate, 60);
        var newerHr = At(Ts.AddHours(1), BiometricType.HeartRate, 65);
        var hrv = At(Ts, BiometricType.HeartRateVariability, 40);
        await repo.IngestBatchAsync([olderHr, newerHr, hrv]);

        var latest = await repo.GetLatestVitalsAsync();

        Assert.Equal(2, latest.Count);
        Assert.Equal(newerHr, latest[BiometricType.HeartRate]);
        Assert.Equal(hrv, latest[BiometricType.HeartRateVariability]);
    }

    [Fact]
    public async Task AddThenDelete_RoundTripsThroughGetById()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);
        var evt = MakeEvent(Guid.NewGuid());

        await repo.AddAsync(evt);
        Assert.Equal(evt, await repo.GetByIdAsync(evt.Id));

        await repo.DeleteAsync(evt.Id);
        Assert.Null(await repo.GetByIdAsync(evt.Id));
    }

    [Fact]
    public async Task Update_OfAnEventStoredEarlier_ReplacesItsValue()
    {
        var evt = MakeEvent(Guid.NewGuid(), value: 60);
        await using (var first = await NewContextAsync())
            await new BiometricRepository(first).AddAsync(evt);

        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);
        await repo.UpdateAsync(evt with { Value = 99 });

        Assert.Equal(99, (await repo.GetByIdAsync(evt.Id))!.Value);
    }

    [Fact]
    public async Task Update_AfterAddOnTheSameContext_ThrowsBecauseTheAddIsStillTracked()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);
        var evt = MakeEvent(Guid.NewGuid(), value: 60);
        await repo.AddAsync(evt);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await repo.UpdateAsync(evt with { Value = 99 }));
    }

    [Fact]
    public async Task Add_AfterAFailedAdd_FailsAgainBecauseTheFailedInsertIsStillTracked()
    {
        await using var db = await NewContextAsync();
        var repo = new BiometricRepository(db);

        // Unit is NOT NULL in the schema, so this save fails inside SQLite.
        await Assert.ThrowsAsync<DbUpdateException>(
            async () => await repo.AddAsync(MakeEvent(Guid.NewGuid(), unit: null!)));

        await Assert.ThrowsAsync<DbUpdateException>(
            async () => await repo.AddAsync(MakeEvent(Guid.NewGuid())));
        Assert.Empty(await StoredAsync(repo));
    }

    [Fact]
    public async Task AuditedRead_WritesOneAuditRowToTheVault()
    {
        await using var db = await NewContextAsync();
        var repo = new AuditLoggingDecorator(
            new BiometricRepository(db), new DbAuditLogger(db), callerIdentity: "tester");

        await repo.QueryRangeAsync(BiometricType.HeartRate, Ts, Ts.AddHours(1));

        var row = Assert.Single(await db.AuditLog.AsNoTracking().ToListAsync());
        Assert.StartsWith("QueryRange: Type=HeartRate", row.Summary);
        Assert.Equal((byte)AuditOperation.Read, row.Operation);
    }
}
