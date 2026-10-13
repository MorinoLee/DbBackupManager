using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteGapProjectionPendings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteGapProjectionPendings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteGapProjectionPendings", x => x.Id);
                    table.CheckConstraint("CK_RemoteGapProjectionPendings_Stage", "[Stage] IS NULL OR [Stage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')");
                    table.ForeignKey(
                        name: "FK_RemoteGapProjectionPendings_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RemoteGapProjectionPendingTargets",
                columns: table => new
                {
                    PendingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteGapProjectionPendingTargets", x => new { x.PendingId, x.StorageTargetId });
                    table.ForeignKey(
                        name: "FK_RemoteGapProjectionPendingTargets_RemoteGapProjectionPendings_PendingId",
                        column: x => x.PendingId,
                        principalTable: "RemoteGapProjectionPendings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RemoteGapProjectionPendingTargets_StorageTargets_StorageTargetId",
                        column: x => x.StorageTargetId,
                        principalTable: "StorageTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_RemoteGapProjectionPendings_DatabaseId",
                table: "RemoteGapProjectionPendings",
                column: "DatabaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteGapProjectionPendingTargets_StorageTargetId",
                table: "RemoteGapProjectionPendingTargets",
                column: "StorageTargetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [RemoteGapProjectionPendings])
                BEGIN
                    THROW 51007, N'已有待补发的远端缺口投影，不能回退。请从迁移前的平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropTable(
                name: "RemoteGapProjectionPendingTargets");

            migrationBuilder.DropTable(
                name: "RemoteGapProjectionPendings");
        }
    }
}
