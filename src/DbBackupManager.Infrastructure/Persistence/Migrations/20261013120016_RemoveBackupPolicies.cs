using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveBackupPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [BackupPolicies])
                    OR EXISTS (SELECT 1 FROM [BackupTasks]
                        WHERE [PolicyId] IS NOT NULL OR [PlanId] IS NULL OR [PlanVersionId] IS NULL)
                BEGIN
                    THROW 51008, N'仍有未迁入统一计划的旧备份策略、策略任务或缺少计划身份的任务，已拒绝删除。', 1;
                END
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_BackupTasks_BackupPolicies_PolicyId",
                table: "BackupTasks");

            migrationBuilder.DropTable(
                name: "BackupPolicies");

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
                name: "PolicyId",
                table: "BackupTasks");

            migrationBuilder.AlterColumn<Guid>(
                name: "PlanVersionId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PlanId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_BackupType",
                table: "BackupTasks",
                sql: "[BackupType] IS NOT NULL AND [BackupType] IN ('Full', 'Differential', 'Log')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_CoveredDifferentialSlot",
                table: "BackupTasks",
                sql: "[CoveredDifferentialSlotUtc] IS NULL OR ([PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL AND [BackupType] IS NOT NULL AND [BackupType] = 'Full' AND DATEPART(TZOFFSET, [CoveredDifferentialSlotUtc]) = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_Identity",
                table: "BackupTasks",
                sql: "[PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_BackupType",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_CoveredDifferentialSlot",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_Identity",
                table: "BackupTasks");

            migrationBuilder.AlterColumn<Guid>(
                name: "PlanVersionId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "PlanId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "PolicyId",
                table: "BackupTasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BackupPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BackupTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    BackupType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DaysOfWeek = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsManualOnly = table.Column<bool>(type: "bit", nullable: false),
                    LocalRetentionDays = table.Column<int>(type: "int", nullable: true),
                    LocalTime = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RemoteRetentionDays = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    ScheduleEffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ScheduleType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    TransferTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UseChecksum = table.Column<bool>(type: "bit", nullable: false),
                    UseCompression = table.Column<bool>(type: "bit", nullable: false),
                    UseCopyOnly = table.Column<bool>(type: "bit", nullable: false),
                    VerifyTimeoutMinutes = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPolicies", x => x.Id);
                    table.CheckConstraint("CK_BackupPolicies_BackupType", "[BackupType] = 'Full'");
                    table.CheckConstraint("CK_BackupPolicies_Name_NotEmpty", "LEN([Name]) > 0");
                    table.CheckConstraint("CK_BackupPolicies_NormalizedName_NotEmpty", "LEN([NormalizedName]) > 0");
                    table.CheckConstraint("CK_BackupPolicies_Schedule", "([ScheduleType] = 'Daily' AND [DaysOfWeek] = 0) OR ([ScheduleType] = 'Weekly' AND [DaysOfWeek] BETWEEN 1 AND 127)");
                    table.CheckConstraint("CK_BackupPolicies_ScheduleEffectiveFrom", "([IsManualOnly] = 1 AND [ScheduleEffectiveFromUtc] IS NULL) OR ([IsManualOnly] = 0 AND [IsEnabled] = 0 AND [ScheduleEffectiveFromUtc] IS NULL) OR ([IsManualOnly] = 0 AND [IsEnabled] = 1 AND [ScheduleEffectiveFromUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_BackupPolicies_Storage", "([StorageMode] = 'LocalOnly' AND [StorageTargetId] IS NULL AND [LocalRetentionDays] BETWEEN 1 AND 36500 AND [RemoteRetentionDays] IS NULL) OR ([StorageMode] = 'LocalAndRemote' AND [StorageTargetId] IS NOT NULL AND [LocalRetentionDays] BETWEEN 1 AND 36500 AND [RemoteRetentionDays] BETWEEN 1 AND 36500) OR ([StorageMode] = 'RemoteOnly' AND [StorageTargetId] IS NOT NULL AND [LocalRetentionDays] IS NULL AND [RemoteRetentionDays] BETWEEN 1 AND 36500)");
                    table.CheckConstraint("CK_BackupPolicies_Timeouts", "[BackupTimeoutMinutes] BETWEEN 1 AND 1440 AND [VerifyTimeoutMinutes] BETWEEN 1 AND 1440 AND [TransferTimeoutMinutes] BETWEEN 1 AND 1440");
                    table.CheckConstraint("CK_BackupPolicies_TimeZoneId_NotEmpty", "LEN([TimeZoneId]) > 0");
                    table.ForeignKey(
                        name: "FK_BackupPolicies_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupPolicies_StorageTargets_StorageTargetId",
                        column: x => x.StorageTargetId,
                        principalTable: "StorageTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_BackupPolicies_StorageTargetId",
                table: "BackupPolicies",
                column: "StorageTargetId");

            migrationBuilder.CreateIndex(
                name: "UX_BackupPolicies_DatabaseId_BackupType_Enabled",
                table: "BackupPolicies",
                columns: new[] { "DatabaseId", "BackupType" },
                unique: true,
                filter: "[IsEnabled] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_BackupPolicies_NormalizedName",
                table: "BackupPolicies",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BackupTasks_BackupPolicies_PolicyId",
                table: "BackupTasks",
                column: "PolicyId",
                principalTable: "BackupPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
