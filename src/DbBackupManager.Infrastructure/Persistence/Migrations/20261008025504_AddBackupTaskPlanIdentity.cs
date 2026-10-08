using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupTaskPlanIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTaskSnapshots_BackupType",
                table: "BackupTaskSnapshots");

            migrationBuilder.DropIndex(
                name: "UX_BackupTasks_PolicyId_ScheduledSlotAtUtc",
                table: "BackupTasks");

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "BackupTaskSnapshots",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PolicyId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "BackupType",
                table: "BackupTasks",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Full");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CoveredDifferentialSlotUtc",
                table: "BackupTasks",
                type: "datetimeoffset(7)",
                precision: 7,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanVersionId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTaskSnapshots_BackupType",
                table: "BackupTaskSnapshots",
                sql: "[BackupType] IS NOT NULL AND [BackupType] IN ('Full', 'Differential', 'Log')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTaskSnapshots_PlanPathVersion",
                table: "BackupTaskSnapshots",
                sql: "[Purpose] IS NULL OR ([Purpose] IS NOT NULL AND [FileNameRuleVersion] IS NOT NULL AND [FileNameRuleVersion] = 'v3')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTaskSnapshots_Purpose",
                table: "BackupTaskSnapshots",
                sql: "([Purpose] IS NULL AND [BackupType] IS NOT NULL AND [BackupType] = 'Full') OR ([Purpose] IS NOT NULL AND [BackupType] IS NOT NULL AND [UseCopyOnly] IS NOT NULL AND (([Purpose] = 'PlanFull' AND [BackupType] = 'Full' AND [UseCopyOnly] = 0) OR ([Purpose] = 'PlanDifferential' AND [BackupType] = 'Differential' AND [UseCopyOnly] = 0) OR ([Purpose] = 'PlanLog' AND [BackupType] = 'Log' AND [UseCopyOnly] = 0) OR ([Purpose] = 'AdHocCopyOnlyFull' AND [BackupType] = 'Full' AND [UseCopyOnly] = 1)))");

            migrationBuilder.CreateIndex(
                name: "UX_BackupTasks_PlanVersion_Type_Slot",
                table: "BackupTasks",
                columns: new[] { "PlanId", "PlanVersionId", "BackupType", "ScheduledSlotAtUtc" },
                unique: true,
                filter: "[PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL AND [BackupType] IS NOT NULL AND [ScheduledSlotAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BackupTasks_PolicyId_ScheduledSlotAtUtc",
                table: "BackupTasks",
                columns: new[] { "PolicyId", "ScheduledSlotAtUtc" },
                unique: true,
                filter: "[PolicyId] IS NOT NULL AND [ScheduledSlotAtUtc] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_BackupType",
                table: "BackupTasks",
                sql: "[BackupType] IS NOT NULL AND [BackupType] IN ('Full', 'Differential', 'Log') AND ([PolicyId] IS NULL OR [BackupType] = 'Full')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_CoveredDifferentialSlot",
                table: "BackupTasks",
                sql: "[CoveredDifferentialSlotUtc] IS NULL OR ([PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL AND [PolicyId] IS NULL AND [BackupType] IS NOT NULL AND [BackupType] = 'Full' AND DATEPART(TZOFFSET, [CoveredDifferentialSlotUtc]) = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_Identity",
                table: "BackupTasks",
                sql: "([PolicyId] IS NOT NULL AND [PlanId] IS NULL AND [PlanVersionId] IS NULL) OR ([PolicyId] IS NULL AND [PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_BackupTasks_BackupPlanVersions_PlanId_PlanVersionId",
                table: "BackupTasks",
                columns: new[] { "PlanId", "PlanVersionId" },
                principalTable: "BackupPlanVersions",
                principalColumns: new[] { "PlanId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [BackupTasks]
                    WHERE [PolicyId] IS NULL OR [PlanId] IS NOT NULL OR [PlanVersionId] IS NOT NULL
                        OR [BackupType] <> 'Full' OR [CoveredDifferentialSlotUtc] IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM [BackupTaskSnapshots] WHERE [Purpose] IS NOT NULL OR [BackupType] <> 'Full')
                BEGIN
                    THROW 51003, N'已写入计划任务或用途快照，不能回退该迁移。请从平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_BackupTasks_BackupPlanVersions_PlanId_PlanVersionId",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTaskSnapshots_BackupType",
                table: "BackupTaskSnapshots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTaskSnapshots_PlanPathVersion",
                table: "BackupTaskSnapshots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTaskSnapshots_Purpose",
                table: "BackupTaskSnapshots");

            migrationBuilder.DropIndex(
                name: "UX_BackupTasks_PlanVersion_Type_Slot",
                table: "BackupTasks");

            migrationBuilder.DropIndex(
                name: "UX_BackupTasks_PolicyId_ScheduledSlotAtUtc",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_BackupType",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_CoveredDifferentialSlot",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_Identity",
                table: "BackupTasks");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "BackupTaskSnapshots");

            migrationBuilder.DropColumn(
                name: "BackupType",
                table: "BackupTasks");

            migrationBuilder.DropColumn(
                name: "CoveredDifferentialSlotUtc",
                table: "BackupTasks");

            migrationBuilder.DropColumn(
                name: "PlanId",
                table: "BackupTasks");

            migrationBuilder.DropColumn(
                name: "PlanVersionId",
                table: "BackupTasks");

            migrationBuilder.AlterColumn<Guid>(
                name: "PolicyId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTaskSnapshots_BackupType",
                table: "BackupTaskSnapshots",
                sql: "[BackupType] = 'Full'");

            migrationBuilder.CreateIndex(
                name: "UX_BackupTasks_PolicyId_ScheduledSlotAtUtc",
                table: "BackupTasks",
                columns: new[] { "PolicyId", "ScheduledSlotAtUtc" },
                unique: true,
                filter: "[ScheduledSlotAtUtc] IS NOT NULL");
        }
    }
}
