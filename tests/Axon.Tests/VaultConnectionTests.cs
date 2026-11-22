using System.Globalization;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Axon.Tests;

/// <summary>
/// Pins the SQLite settings the vault's connections run with once
/// <see cref="AxonDbContextFactory"/> has created and migrated the database.
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
        var connection = db.Database.GetDbConnection();
        bool wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {name};";
            return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }

    private static async Task AssertTunedAsync(AxonDbContext db)
    {
        Assert.Equal("wal", await PragmaAsync(db, "journal_mode"));
        Assert.Equal("1", await PragmaAsync(db, "synchronous"));      // NORMAL
        Assert.Equal("-32000", await PragmaAsync(db, "cache_size"));
        Assert.Equal("1", await PragmaAsync(db, "foreign_keys"));
    }

    [Fact]
    public async Task Vault_ConnectionRunsWithWalAndTheTunedPragmas()
    {
        await using var db = await new AxonDbContextFactory(new MockHardwareVault()).CreateAsync(_dir);

        await AssertTunedAsync(db);
    }
}
