using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupPlanBaselineEnablements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupPlanBaselineEnablements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MutationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPlanBaselineEnablements", x => x.Id);
                    table.CheckConstraint("CK_BackupPlanBaselineEnablements_Decision", "([Decision] = 'Queued' AND [TaskId] IS NOT NULL) OR ([Decision] IN ('SkippedManagedBaseline', 'SkippedUncertainBaseline', 'SkippedOutstandingFull') AND [TaskId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_BackupPlanBaselineEnablements_BackupPlanVersions_PlanId_PlanVersionId",
                        columns: x => new { x.PlanId, x.PlanVersionId },
                        principalTable: "BackupPlanVersions",
                        principalColumns: new[] { "PlanId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupPlanBaselineEnablements_BackupTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "BackupTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_BackupPlanBaselineEnablements_MutationId",
                table: "BackupPlanBaselineEnablements",
                column: "MutationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BackupPlanBaselineEnablements_PlanVersion",
                table: "BackupPlanBaselineEnablements",
                columns: new[] { "PlanId", "PlanVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BackupPlanBaselineEnablements_TaskId",
                table: "BackupPlanBaselineEnablements",
                column: "TaskId",
                unique: true,
                filter: "[TaskId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [BackupPlanBaselineEnablements])
                BEGIN
                    THROW 51009, N'已有计划启用补基线记录，不能回退。请从迁移前的平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropTable(
                name: "BackupPlanBaselineEnablements");
        }
    }
}
