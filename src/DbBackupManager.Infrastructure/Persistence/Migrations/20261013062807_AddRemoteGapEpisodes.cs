using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteGapEpisodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteGapEpisodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GapCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RecoveredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteGapEpisodes", x => x.Id);
                    table.CheckConstraint("CK_RemoteGapEpisodes_GapCode_NotEmpty", "LEN([GapCode]) > 0");
                    table.CheckConstraint("CK_RemoteGapEpisodes_RecoveredAt", "[RecoveredAtUtc] IS NULL OR [RecoveredAtUtc] >= [OpenedAtUtc]");
                    table.ForeignKey(
                        name: "FK_RemoteGapEpisodes_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RemoteGapEpisodes_NotificationOutbox_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "NotificationOutbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RemoteGapEpisodes_StorageTargets_StorageTargetId",
                        column: x => x.StorageTargetId,
                        principalTable: "StorageTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteGapEpisodes_StorageTargetId",
                table: "RemoteGapEpisodes",
                column: "StorageTargetId");

            migrationBuilder.CreateIndex(
                name: "UX_RemoteGapEpisodes_NotificationId",
                table: "RemoteGapEpisodes",
                column: "NotificationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RemoteGapEpisodes_Open",
                table: "RemoteGapEpisodes",
                columns: new[] { "DatabaseId", "StorageTargetId", "GapCode" },
                unique: true,
                filter: "[RecoveredAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [RemoteGapEpisodes])
                BEGIN
                    THROW 51006, N'已有远端缺口通知记录，不能回退。请从迁移前的平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropTable(
                name: "RemoteGapEpisodes");
        }
    }
}
