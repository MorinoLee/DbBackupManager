using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsPaused = table.Column<bool>(type: "bit", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPlans", x => x.Id);
                    table.CheckConstraint("CK_BackupPlans_Name_NotEmpty", "LEN([Name]) > 0");
                    table.CheckConstraint("CK_BackupPlans_NormalizedName_NotEmpty", "LEN([NormalizedName]) > 0");
                    table.ForeignKey(
                        name: "FK_BackupPlans_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupPlanVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    StorageMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocalRecoveryWindowDays = table.Column<int>(type: "int", nullable: true),
                    RemoteRecoveryWindowDays = table.Column<int>(type: "int", nullable: true),
                    UseChecksum = table.Column<bool>(type: "bit", nullable: false),
                    UseCompression = table.Column<bool>(type: "bit", nullable: false),
                    BackupTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    VerifyTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    TransferTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    DifferentialSchedule_DaysOfWeek = table.Column<int>(type: "int", nullable: true),
                    DifferentialSchedule_LocalTime = table.Column<TimeOnly>(type: "time(0)", nullable: true),
                    DifferentialSchedule_ScheduleType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FullSchedule_DaysOfWeek = table.Column<int>(type: "int", nullable: false),
                    FullSchedule_LocalTime = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    FullSchedule_ScheduleType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LogSchedule_AnchorUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    LogSchedule_IntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPlanVersions", x => x.Id);
                    table.UniqueConstraint("AK_BackupPlanVersions_PlanId_Id", x => new { x.PlanId, x.Id });
                    table.CheckConstraint("CK_BackupPlanVersions_LogInterval", "([LogSchedule_IntervalMinutes] IS NULL AND [LogSchedule_AnchorUtc] IS NULL) OR ([LogSchedule_IntervalMinutes] IS NOT NULL AND [LogSchedule_IntervalMinutes] BETWEEN 1 AND 1440 AND [LogSchedule_AnchorUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_BackupPlanVersions_ModeSchedules", "([Mode] = 'Full' AND [DifferentialSchedule_ScheduleType] IS NULL AND [LogSchedule_IntervalMinutes] IS NULL AND [LogSchedule_AnchorUtc] IS NULL) OR ([Mode] = 'FullAndDifferential' AND [DifferentialSchedule_ScheduleType] IS NOT NULL AND [LogSchedule_IntervalMinutes] IS NULL AND [LogSchedule_AnchorUtc] IS NULL) OR ([Mode] = 'FullAndDifferentialAndLog' AND [DifferentialSchedule_ScheduleType] IS NOT NULL AND [LogSchedule_IntervalMinutes] IS NOT NULL AND [LogSchedule_AnchorUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_BackupPlanVersions_Number", "[Number] >= 1");
                    table.CheckConstraint("CK_BackupPlanVersions_Schedule", "([FullSchedule_ScheduleType] IS NOT NULL AND [FullSchedule_LocalTime] IS NOT NULL AND [FullSchedule_DaysOfWeek] IS NOT NULL AND [FullSchedule_ScheduleType] IN ('Daily', 'Weekly') AND (([FullSchedule_ScheduleType] = 'Daily' AND [FullSchedule_DaysOfWeek] = 0) OR ([FullSchedule_ScheduleType] = 'Weekly' AND [FullSchedule_DaysOfWeek] BETWEEN 1 AND 127))) AND (([DifferentialSchedule_ScheduleType] IS NULL AND [DifferentialSchedule_LocalTime] IS NULL AND [DifferentialSchedule_DaysOfWeek] IS NULL) OR ([DifferentialSchedule_ScheduleType] IS NOT NULL AND [DifferentialSchedule_LocalTime] IS NOT NULL AND [DifferentialSchedule_DaysOfWeek] IS NOT NULL AND [DifferentialSchedule_ScheduleType] IN ('Daily', 'Weekly') AND (([DifferentialSchedule_ScheduleType] = 'Daily' AND [DifferentialSchedule_DaysOfWeek] = 0) OR ([DifferentialSchedule_ScheduleType] = 'Weekly' AND [DifferentialSchedule_DaysOfWeek] BETWEEN 1 AND 127))))");
                    table.CheckConstraint("CK_BackupPlanVersions_Timeouts", "[BackupTimeoutMinutes] BETWEEN 1 AND 1440 AND [VerifyTimeoutMinutes] BETWEEN 1 AND 1440 AND [TransferTimeoutMinutes] BETWEEN 1 AND 1440");
                    table.CheckConstraint("CK_BackupPlanVersions_TimeZoneId_NotEmpty", "LEN([TimeZoneId]) > 0");
                    table.CheckConstraint("CK_BackupPlanVersions_UtcOffset", "DATEPART(TZOFFSET, [EffectiveFromUtc]) = 0 AND ([LogSchedule_AnchorUtc] IS NULL OR DATEPART(TZOFFSET, [LogSchedule_AnchorUtc]) = 0)");
                    table.CheckConstraint("CK_BackupPlanVersions_Windows", "([StorageMode] = 'LocalOnly' AND [StorageTargetId] IS NULL AND [LocalRecoveryWindowDays] IS NOT NULL AND [LocalRecoveryWindowDays] BETWEEN 1 AND 36500 AND [RemoteRecoveryWindowDays] IS NULL) OR ([StorageMode] = 'LocalAndRemote' AND [StorageTargetId] IS NOT NULL AND [LocalRecoveryWindowDays] IS NOT NULL AND [LocalRecoveryWindowDays] BETWEEN 1 AND 36500 AND [RemoteRecoveryWindowDays] IS NOT NULL AND [RemoteRecoveryWindowDays] BETWEEN 1 AND 36500) OR ([StorageMode] = 'RemoteOnly' AND [StorageTargetId] IS NOT NULL AND [LocalRecoveryWindowDays] IS NULL AND [RemoteRecoveryWindowDays] IS NOT NULL AND [RemoteRecoveryWindowDays] BETWEEN 1 AND 36500)");
                    table.ForeignKey(
                        name: "FK_BackupPlanVersions_BackupPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "BackupPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupPlanVersions_StorageTargets_StorageTargetId",
                        column: x => x.StorageTargetId,
                        principalTable: "StorageTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlans_Id_CurrentVersionId",
                table: "BackupPlans",
                columns: new[] { "Id", "CurrentVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_BackupPlans_DatabaseId",
                table: "BackupPlans",
                column: "DatabaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlanVersions_StorageTargetId",
                table: "BackupPlanVersions",
                column: "StorageTargetId");

            migrationBuilder.CreateIndex(
                name: "UX_BackupPlanVersions_PlanId_Number",
                table: "BackupPlanVersions",
                columns: new[] { "PlanId", "Number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BackupPlans_BackupPlanVersions_Id_CurrentVersionId",
                table: "BackupPlans",
                columns: new[] { "Id", "CurrentVersionId" },
                principalTable: "BackupPlanVersions",
                principalColumns: new[] { "PlanId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackupPlanMigrationGuard.RejectDownWhenPlansExist);

            migrationBuilder.DropForeignKey(
                name: "FK_BackupPlans_BackupPlanVersions_Id_CurrentVersionId",
                table: "BackupPlans");

            migrationBuilder.DropTable(
                name: "BackupPlanVersions");

            migrationBuilder.DropTable(
                name: "BackupPlans");
        }
    }
}
