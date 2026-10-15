using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupPlanEnablementSequenceAndBaselineTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_Trigger",
                table: "BackupTasks");

            migrationBuilder.DropIndex(
                name: "UX_BackupPlanBaselineEnablements_PlanVersion",
                table: "BackupPlanBaselineEnablements");

            migrationBuilder.AddColumn<int>(
                name: "EnablementSequence",
                table: "BackupPlanBaselineEnablements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE [target]
                SET [EnablementSequence] = [numbered].[Seq]
                FROM [BackupPlanBaselineEnablements] AS [target]
                INNER JOIN (
                    SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [PlanId] ORDER BY [CreatedAtUtc], [Id]) AS [Seq]
                    FROM [BackupPlanBaselineEnablements]
                ) AS [numbered] ON [target].[Id] = [numbered].[Id];
                """);

            migrationBuilder.AlterColumn<int>(
                name: "EnablementSequence",
                table: "BackupPlanBaselineEnablements",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: false,
                oldDefaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_Trigger",
                table: "BackupTasks",
                sql: "([TriggerType] = 'Scheduled' AND [ScheduledSlotAtUtc] IS NOT NULL) OR ([TriggerType] IN ('Manual', 'BaselineSupplement') AND [ScheduledSlotAtUtc] IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlanBaselineEnablements_PlanVersion",
                table: "BackupPlanBaselineEnablements",
                columns: new[] { "PlanId", "PlanVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_BackupPlanBaselineEnablements_Sequence",
                table: "BackupPlanBaselineEnablements",
                columns: new[] { "PlanId", "EnablementSequence" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanBaselineEnablements_Sequence",
                table: "BackupPlanBaselineEnablements",
                sql: "[EnablementSequence] >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [BackupTasks] WHERE [TriggerType] = N'BaselineSupplement')
                BEGIN
                    THROW 51011, N'已有启用补基线任务，不能回退触发类型。请从迁移前的平台库备份恢复。', 1;
                END
                IF EXISTS (
                    SELECT 1
                    FROM [BackupPlanBaselineEnablements]
                    GROUP BY [PlanId], [PlanVersionId]
                    HAVING COUNT(*) > 1)
                BEGIN
                    THROW 51010, N'同一计划版本已有多次启用记录，不能回退启用序号。请从迁移前的平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_Trigger",
                table: "BackupTasks");

            migrationBuilder.DropIndex(
                name: "IX_BackupPlanBaselineEnablements_PlanVersion",
                table: "BackupPlanBaselineEnablements");

            migrationBuilder.DropIndex(
                name: "UX_BackupPlanBaselineEnablements_Sequence",
                table: "BackupPlanBaselineEnablements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanBaselineEnablements_Sequence",
                table: "BackupPlanBaselineEnablements");

            migrationBuilder.DropColumn(
                name: "EnablementSequence",
                table: "BackupPlanBaselineEnablements");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_Trigger",
                table: "BackupTasks",
                sql: "([TriggerType] = 'Scheduled' AND [ScheduledSlotAtUtc] IS NOT NULL) OR ([TriggerType] = 'Manual' AND [ScheduledSlotAtUtc] IS NULL)");

            migrationBuilder.CreateIndex(
                name: "UX_BackupPlanBaselineEnablements_PlanVersion",
                table: "BackupPlanBaselineEnablements",
                columns: new[] { "PlanId", "PlanVersionId" },
                unique: true);
        }
    }
}
