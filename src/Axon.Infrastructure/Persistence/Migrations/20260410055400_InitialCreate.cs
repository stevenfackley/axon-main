using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Axon.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Operation = table.Column<byte>(type: "INTEGER", nullable: false),
                    RepositoryName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CallerIdentity = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AffectedEntityId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BiometricEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FirmwareVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ConfidenceScore = table.Column<float>(type: "REAL", nullable: false),
                    IngestionMs = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiometricEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BiometricEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SerializedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ProcessedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_EntityId",
                table: "AuditLog",
                column: "AffectedEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_OccurredAt",
                table: "AuditLog",
                column: "OccurredAtUnixMs");

            migrationBuilder.CreateIndex(
                name: "IX_BiometricEvents_CorrelationId",
                table: "BiometricEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_BiometricEvents_Type_Timestamp",
                table: "BiometricEvents",
                columns: new[] { "Type", "TimestampMs" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_CreatedAt",
                table: "SyncOutbox",
                column: "CreatedAtUnixMs");

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_Pending",
                table: "SyncOutbox",
                column: "ProcessedAtUnixMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "BiometricEvents");

            migrationBuilder.DropTable(
                name: "SyncOutbox");
        }
    }
}
