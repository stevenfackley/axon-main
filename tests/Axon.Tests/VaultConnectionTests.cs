using System.Globalization;
using Axon.Core.Domain;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Axon.Tests;

/// <summary>
/// Pins the SQLite settings every vault connection runs with once
/// <see cref="AxonDbContextFactory"/> has created and migrated the database, and that
/// each repository call gets its own context.
/// </summary>
public sealed class VaultConnectionTests : IDisposable
{
    private readonly string _dir;

    public VaultConnectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "axon-vaultconn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static async Task<string> PragmaAsync(AxonDbContext db, string name)
    {
        // Opened through EF Core, as the repositories do, not on the raw ADO.NET connection.
        await db.Database.OpenConnectionAsync();
        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA {name};";
            return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task AssertTunedAsync(AxonDbContext db)
    {
        Assert.Equal("wal", await PragmaAsync(db, "journal_mode"));
        Assert.Equal("1", await PragmaAsync(db, "synchronous"));      // NORMAL
        Assert.Equal("-32000", await PragmaAsync(db, "cache_size"));
        Assert.Equal("1", await PragmaAsync(db, "foreign_keys"));
    }

    private async Task<AxonDbContextFactory> NewVaultAsync()
    {
        var factory = new AxonDbContextFactory(new MockHardwareVault());
        await factory.InitialiseAsync(_dir);
        return factory;
    }

    [Fact]
    public async Task Vault_ConnectionRunsWithWalAndTheTunedPragmas()
    {
        var factory = await NewVaultAsync();
        await using var db = factory.CreateDbContext();

        await AssertTunedAsync(db);
    }

    [Fact]
    public async Task Vault_ASecondConnectionOpenedWhileTheFirstIsBusy_GetsTheSamePragmas()
    {
        var factory = await NewVaultAsync();
        await using var first = factory.CreateDbContext();
        await first.Database.OpenConnectionAsync(); // holds the pooled connection

        await using var second = factory.CreateDbContext();
        await AssertTunedAsync(second);
    }

    [Fact]
    public void CreateDbContext_BeforeInitialise_Throws()
    {
        var factory = new AxonDbContextFactory(new MockHardwareVault());

        Assert.Throws<InvalidOperationException>(() => factory.CreateDbContext());
    }

    [Fact]
    public async Task Repositories_UsedFromTwoThreadsAtOnce_DoNotShareAContext()
    {
        var factory = await NewVaultAsync();
        var events = new BiometricRepository(factory);
        var outbox = new SyncOutboxRepository(factory);
        var ts = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

        var writer = Task.Run(async () =>
        {
            for (int i = 0; i < 25; i++)
            {
                await events.IngestBatchAsync(
                [
                    new BiometricEvent(Guid.NewGuid(), ts.AddMinutes(i), BiometricType.HeartRate, 60, "bpm",
                        new SourceMetadata("dev-1", "Test", null, 0.9f, ts)),
                ]);
            }
        });
        var reader = Task.Run(async () =>
        {
            for (int i = 0; i < 25; i++)
                await outbox.CountPendingAsync();
        });

        await Task.WhenAll(writer, reader);

        Assert.Equal(25, await outbox.CountPendingAsync());
    }
}
