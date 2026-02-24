using Axon.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Axon.Infrastructure.Persistence;

/// <summary>
/// EF Core database context for Axon's SQLCipher-encrypted SQLite vault.
///
/// Configuration notes:
///   • WAL mode is enabled at connection time (see <see cref="AxonDbContextFactory"/>)
///     so background relay reads never block UI writes.
///   • The encryption passphrase is injected via the connection string as
///     <c>Password=&lt;hex-key&gt;</c>; the hex key is derived by
///     <see cref="Axon.Core.Ports.IHardwareVault"/> and NEVER appears in
///     application config files or environment variables.
///   • All fluent configuration uses explicit column names/types to support
///     the EF Core compiled-model source generator (AOT requirement).
/// </summary>
public sealed class AxonDbContext(DbContextOptions<AxonDbContext> options)
    : DbContext(options)
{
    internal DbSet<BiometricEventEntity> BiometricEvents { get; set; } = null!;
    internal DbSet<SyncOutboxEntity>     SyncOutbox      { get; set; } = null!;
    internal DbSet<AuditLogEntity>       AuditLog        { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── BiometricEvents ───────────────────────────────────────────────────
        mb.Entity<BiometricEventEntity>(e =>
        {
            e.ToTable("BiometricEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id)
             .HasColumnType("TEXT")
             .IsRequired();
            e.Property(x => x.TimestampUnixMs)
             .HasColumnName("TimestampMs")
             .HasColumnType("INTEGER")
             .IsRequired();
            e.Property(x => x.BiometricType)
             .HasColumnName("Type")
             .HasColumnType("INTEGER")
             .IsRequired();
            e.Property(x => x.Value)
             .HasColumnType("REAL")
             .IsRequired();
            e.Property(x => x.Unit)
             .HasColumnType("TEXT")
             .HasMaxLength(16)
             .IsRequired();
            e.Property(x => x.DeviceId)
             .HasColumnType("TEXT")
             .HasMaxLength(512)   // Encrypted; ciphertext is longer than plaintext
             .IsRequired();
            e.Property(x => x.Vendor)
             .HasColumnType("TEXT")
             .HasMaxLength(64)
             .IsRequired();
            e.Property(x => x.FirmwareVersion)
             .HasColumnType("TEXT")
             .HasMaxLength(32);
            e.Property(x => x.ConfidenceScore)
             .HasColumnType("REAL")
             .IsRequired();
            e.Property(x => x.IngestionTimestampUnixMs)
             .HasColumnName("IngestionMs")
             .HasColumnType("INTEGER")
             .IsRequired();
            e.Property(x => x.CorrelationId)
             .HasColumnType("TEXT")
             .HasMaxLength(64);

            // Composite index for O(log n) time-series range queries
            e.HasIndex(x => new { x.BiometricType, x.TimestampUnixMs })
             .HasDatabaseName("IX_BiometricEvents_Type_Timestamp");

            // Outbox correlation lookup
            e.HasIndex(x => x.CorrelationId)
             .HasDatabaseName("IX_BiometricEvents_CorrelationId");
        });

        // ── SyncOutbox ────────────────────────────────────────────────────────
        mb.Entity<SyncOutboxEntity>(e =>
        {
            e.ToTable("SyncOutbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.BiometricEventId).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.CorrelationId).HasColumnType("TEXT").HasMaxLength(64).IsRequired();
            e.Property(x => x.SerializedPayload).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.CreatedAtUnixMs).HasColumnType("INTEGER").IsRequired();
            e.Property(x => x.ProcessedAtUnixMs).HasColumnType("INTEGER");
            e.Property(x => x.RetryCount).HasColumnType("INTEGER").IsRequired();
            e.Property(x => x.LastError).HasColumnType("TEXT").HasMaxLength(2048);

            // Relay service polls pending (unprocessed) entries ordered oldest-first
            e.HasIndex(x => x.ProcessedAtUnixMs)
             .HasDatabaseName("IX_SyncOutbox_Pending");
            e.HasIndex(x => x.CreatedAtUnixMs)
             .HasDatabaseName("IX_SyncOutbox_CreatedAt");
        });

        // ── AuditLog (append-only) ────────────────────────────────────────────
        mb.Entity<AuditLogEntity>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.OccurredAtUnixMs).HasColumnType("INTEGER").IsRequired();
            e.Property(x => x.Operation).HasColumnType("INTEGER").IsRequired();
            e.Property(x => x.RepositoryName).HasColumnType("TEXT").HasMaxLength(128).IsRequired();
            e.Property(x => x.CallerIdentity).HasColumnType("TEXT").HasMaxLength(64).IsRequired();
            e.Property(x => x.AffectedEntityId).HasColumnType("TEXT").HasMaxLength(64);
            e.Property(x => x.Summary).HasColumnType("TEXT").HasMaxLength(512).IsRequired();

            e.HasIndex(x => x.AffectedEntityId)
             .HasDatabaseName("IX_AuditLog_EntityId");
            e.HasIndex(x => x.OccurredAtUnixMs)
             .HasDatabaseName("IX_AuditLog_OccurredAt");
        });
    }
}
