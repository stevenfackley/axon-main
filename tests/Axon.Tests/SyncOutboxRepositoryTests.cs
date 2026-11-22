using Axon.Core.Domain;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;

namespace Axon.Tests;

/// <summary>
/// Integration tests for <see cref="SyncOutboxRepository"/> against a real encrypted
/// SQLite vault (created + migrated via AxonDbContextFactory): the queries and updates
/// the outbox relay drives.
/// </summary>
public sealed class SyncOutboxRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Ts = new(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir;

    public SyncOutboxRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "axon-outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<AxonDbContext> NewContextAsync() =>
        await new AxonDbContextFactory(new MockHardwareVault()).CreateAsync(_dir);

    private static SyncOutboxEntry Entry(DateTimeOffset createdAt, DateTimeOffset? processedAt = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "corr-1", "{}", createdAt, processedAt, 0, null);

    [Fact]
    public async Task GetPending_ReturnsUnprocessedEntriesOldestFirstUpToTheBatchSize()
    {
        await using var db = await NewContextAsync();
        var repo = new SyncOutboxRepository(db);
        var newest = Entry(Ts.AddMinutes(2));
        var oldest = Entry(Ts);
        var middle = Entry(Ts.AddMinutes(1));
        var processed = Entry(Ts.AddMinutes(-5), processedAt: Ts);
        await repo.AddRangeAsync([newest, oldest, middle, processed]);

        var pending = await repo.GetPendingAsync(batchSize: 2);

        Assert.Equal([oldest, middle], pending);
        Assert.Equal(3, await repo.CountPendingAsync());
    }

    [Fact]
    public async Task MarkProcessed_TakesTheEntryOutOfPending()
    {
        await using var db = await NewContextAsync();
        var repo = new SyncOutboxRepository(db);
        var sent = Entry(Ts);
        var waiting = Entry(Ts.AddMinutes(1));
        await repo.AddRangeAsync([sent, waiting]);

        await repo.MarkProcessedAsync(sent.Id);

        Assert.Equal([waiting], await repo.GetPendingAsync());
        Assert.NotNull((await repo.GetByIdAsync(sent.Id))!.ProcessedAt);
    }

    [Fact]
    public async Task MarkFailed_CountsRetriesKeepsTheLatestErrorAndStaysPending()
    {
        await using var db = await NewContextAsync();
        var repo = new SyncOutboxRepository(db);
        var entry = Entry(Ts);
        await repo.AddAsync(entry);

        await repo.MarkFailedAsync(entry.Id, "first");
        await repo.MarkFailedAsync(entry.Id, "second");

        var stored = await repo.GetByIdAsync(entry.Id);
        Assert.Equal(entry with { RetryCount = 2, LastError = "second" }, stored);
        Assert.Equal(1, await repo.CountPendingAsync());
    }

    [Fact]
    public async Task PurgeOld_DeletesOnlyEntriesProcessedBeforeTheRetentionCutoff()
    {
        await using var db = await NewContextAsync();
        var repo = new SyncOutboxRepository(db);
        var longAgo = Entry(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            processedAt: new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var notYetDue = Entry(Ts, processedAt: new DateTimeOffset(2999, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var unprocessed = Entry(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await repo.AddRangeAsync([longAgo, notYetDue, unprocessed]);

        await repo.PurgeOldAsync(retentionDays: 30);

        Assert.Null(await repo.GetByIdAsync(longAgo.Id));
        Assert.NotNull(await repo.GetByIdAsync(notYetDue.Id));
        Assert.NotNull(await repo.GetByIdAsync(unprocessed.Id));
    }

    [Fact]
    public async Task Delete_RemovesTheEntry()
    {
        await using var db = await NewContextAsync();
        var repo = new SyncOutboxRepository(db);
        var entry = Entry(Ts);
        await repo.AddAsync(entry);

        await repo.DeleteAsync(entry.Id);

        Assert.Null(await repo.GetByIdAsync(entry.Id));
        Assert.Equal(0, await repo.CountPendingAsync());
    }
}
