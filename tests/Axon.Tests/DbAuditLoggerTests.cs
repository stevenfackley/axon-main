using System.Security.Cryptography;
using Axon.Core.Domain;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;

namespace Axon.Tests;

/// <summary>
/// Integration tests for <see cref="DbAuditLogger"/> against a real encrypted SQLite
/// vault (created + migrated via AxonDbContextFactory).
/// </summary>
public sealed class DbAuditLoggerTests : IDisposable
{
    private readonly string _dir;

    public DbAuditLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "axon-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<AxonDbContextFactory> NewVaultAsync()
    {
        var factory = new AxonDbContextFactory(new MockHardwareVault());
        await factory.InitialiseAsync(_dir);
        return factory;
    }

    [Fact]
    public async Task Log_StoresTheCallerAsASha256Hash_AndGetTrailFiltersByEntity()
    {
        var db = await NewVaultAsync();
        var logger = new DbAuditLogger(db);

        await logger.LogAsync(AuditOperation.Read, "Repo", "test-user", "entity-1", "first read");
        await logger.LogAsync(AuditOperation.Write, "Repo", "test-user", "entity-2", "other write");

        var entry = Assert.Single(await logger.GetTrailAsync("entity-1"));
        Assert.Equal(AuditOperation.Read, entry.Operation);
        Assert.Equal("Repo", entry.RepositoryName);
        Assert.Equal("entity-1", entry.AffectedEntityId);
        Assert.Equal("first read", entry.Summary);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData("test-user"u8)).ToLowerInvariant(),
            entry.CallerIdentity);
    }

    [Fact]
    public async Task GetTrail_ForAnUnknownEntity_IsEmpty()
    {
        var db = await NewVaultAsync();
        var logger = new DbAuditLogger(db);
        await logger.LogAsync(AuditOperation.Read, "Repo", "test-user", "entity-1", "read");

        Assert.Empty(await logger.GetTrailAsync("entity-9"));
    }
}
