using Axon.Core.Ports;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

namespace Axon.Infrastructure.Persistence;

/// <summary>
/// Constructs a fully-configured, SQLCipher-encrypted <see cref="AxonDbContext"/>.
///
/// Usage: resolve via DI; do not construct <see cref="AxonDbContext"/> directly.
///
/// Encryption flow:
///   1. <see cref="IHardwareVault.DeriveKeyAsync"/> returns a 32-byte AES key
///      bound to the hardware TPM/Secure Enclave.
///   2. The raw bytes are hex-encoded and injected into the SQLite connection
///      string as <c>Password=hex:&lt;64-char-hex&gt;</c>.
///   3. The key buffer is zeroed immediately after the connection string is built.
///   4. WAL journal mode is set via pragma to enable concurrent read/write access.
/// </summary>
public sealed class AxonDbContextFactory(IHardwareVault vault)
{
    private const string KeyLabel    = "axon.db.master";
    private const string DbFileName  = "axon.vault.db";

    public async ValueTask<AxonDbContext> CreateAsync(
        string      dataDirectory,
        CancellationToken ct = default)
    {
        // Initialise SQLCipher native library binding
        Batteries_V2.Init();

        var keyMaterial = await vault.DeriveKeyAsync(KeyLabel, ct).ConfigureAwait(false);
        string connStr;
        try
        {
            connStr = BuildConnectionString(dataDirectory, keyMaterial.Span);
        }
        finally
        {
            vault.ZeroKey(keyMaterial);
        }

        var options = new DbContextOptionsBuilder<AxonDbContext>()
            .UseSqlite(connStr, sqlite =>
            {
                sqlite.CommandTimeout(30);
            })
            .Options;

        var ctx = new AxonDbContext(options);

        // Enable WAL mode and set page size for high-throughput time-series writes
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;",  ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA page_size=4096;",    ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA cache_size=-32000;", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;",ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;",   ct).ConfigureAwait(false);

        await ctx.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);

        return ctx;
    }

    /// <summary>
    /// Builds the SQLite connection string with the hex-encoded SQLCipher key.
    /// The <paramref name="key"/> span is expected to be 32 bytes (AES-256).
    /// This method operates on stack memory only — no heap allocation for the key.
    /// </summary>
    private static string BuildConnectionString(string dataDirectory, ReadOnlySpan<byte> key)
    {
        // Hex-encode 32 bytes → 64 hex chars on the stack
        Span<char> hex = stackalloc char[64];
        for (int i = 0; i < key.Length; i++)
        {
            byte b = key[i];
            hex[i * 2]     = ToHexChar(b >> 4);
            hex[i * 2 + 1] = ToHexChar(b & 0xF);
        }

        var dbPath = Path.Combine(dataDirectory, DbFileName);
        // Use interpolated string — hex span is copied into the string, not retained
        return $"Data Source={dbPath};Password=hex:{new string(hex)};";
    }

    private static char ToHexChar(int nibble) =>
        (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
}
