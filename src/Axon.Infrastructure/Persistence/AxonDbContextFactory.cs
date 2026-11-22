using Axon.Core.Ports;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

namespace Axon.Infrastructure.Persistence;

/// <summary>
/// Opens Axon's SQLCipher-encrypted vault once, then hands out a short-lived
/// <see cref="AxonDbContext"/> per operation.
///
/// Usage: call <see cref="InitialiseAsync"/> once at start-up, then pass the factory to the
/// repositories. Each repository method creates, uses and disposes its own context, so no
/// change tracker or connection is shared between callers or threads.
///
/// Encryption flow:
///   1. <see cref="IHardwareVault.DeriveKeyAsync"/> returns a 32-byte AES key
///      bound to the hardware TPM/Secure Enclave.
///   2. The raw bytes are hex-encoded and injected into the SQLite connection
///      string as <c>Password=hex:&lt;64-char-hex&gt;</c>.
///   3. The key buffer is zeroed immediately after the connection string is built.
///   4. WAL journal mode is set via pragma to enable concurrent read/write access;
///      the per-connection pragmas are applied to every connection by
///      <see cref="ConnectionPragmaInterceptor"/>.
/// </summary>
public sealed class AxonDbContextFactory(IHardwareVault vault) : IDbContextFactory<AxonDbContext>
{
    private const string KeyLabel = "axon.db.master";
    private const string DbFileName = "axon.vault.db";

    private DbContextOptions<AxonDbContext>? _options;

    /// <summary>
    /// Derives the vault key, applies the database-level pragmas and runs the migrations.
    /// Call once before <see cref="CreateDbContext"/>.
    /// </summary>
    public async ValueTask InitialiseAsync(
        string dataDirectory,
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

        var options = new DbContextOptionsBuilder<AxonDbContext>(CreateOptions(connStr))
            .AddInterceptors(ConnectionPragmaInterceptor.Instance)
            .Options;

        var ctx = new AxonDbContext(options);
        await using (ctx.ConfigureAwait(false))
        {
            await ConfigureDatabaseAsync(ctx, ct).ConfigureAwait(false);
            await ctx.Database.MigrateAsync(ct).ConfigureAwait(false);
        }

        _options = options;
    }

    /// <summary>
    /// A new context on the vault opened by <see cref="InitialiseAsync"/>. The caller disposes it.
    /// </summary>
    public AxonDbContext CreateDbContext() =>
        new(_options ?? throw new InvalidOperationException(
            "The vault is not open. Call InitialiseAsync before creating a context."));

    internal static DbContextOptions<AxonDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<AxonDbContext>()
            .UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(30))
            .Options;

    /// <summary>
    /// Builds the SQLite connection string with the hex-encoded SQLCipher key.
    /// The <paramref name="key"/> span is expected to be 32 bytes (AES-256).
    /// This method operates on stack memory only — no heap allocation for the key.
    /// </summary>
    internal static string BuildConnectionString(string dataDirectory, ReadOnlySpan<byte> key)
    {
        // Hex-encode 32 bytes → 64 hex chars on the stack
        Span<char> hex = stackalloc char[64];
        for (int i = 0; i < key.Length; i++)
        {
            byte b = key[i];
            hex[i * 2] = ToHexChar(b >> 4);
            hex[i * 2 + 1] = ToHexChar(b & 0xF);
        }

        var dbPath = Path.Combine(dataDirectory, DbFileName);
        // Use interpolated string — hex span is copied into the string, not retained
        return $"Data Source={dbPath};Password=hex:{new string(hex)};";
    }

    private static async Task ConfigureDatabaseAsync(AxonDbContext context, CancellationToken ct)
    {
        // Database-level settings, kept in the file: enable WAL mode and set the page size
        // for high-throughput time-series writes. The per-connection settings (cache size,
        // synchronous, foreign keys) come from ConnectionPragmaInterceptor on every open.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA page_size=4096;", ct).ConfigureAwait(false);
    }

    private static char ToHexChar(int nibble) =>
        (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
}
