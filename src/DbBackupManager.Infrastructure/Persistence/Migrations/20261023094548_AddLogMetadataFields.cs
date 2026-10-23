using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLogMetadataFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BeginsLogChain",
                table: "BackupSets",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeginsLogChainState",
                table: "BackupSets",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "HasBulkLoggedData",
                table: "BackupSets",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HasBulkLoggedDataState",
                table: "BackupSets",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "IsForceOffline",
                table: "BackupSets",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsForceOfflineState",
                table: "BackupSets",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "RecoveryModel",
                table: "BackupSets",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryModelState",
                table: "BackupSets",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "BeginsLogChain",
                table: "BackupSetEvidence",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeginsLogChainState",
                table: "BackupSetEvidence",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "HasBulkLoggedData",
                table: "BackupSetEvidence",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HasBulkLoggedDataState",
                table: "BackupSetEvidence",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "IsForceOffline",
                table: "BackupSetEvidence",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsForceOfflineState",
                table: "BackupSetEvidence",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "RecoveryModel",
                table: "BackupSetEvidence",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryModelState",
                table: "BackupSetEvidence",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SourceObservedAtUtc",
                table: "BackupSetEvidence",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BeginsLogChain",
                table: "BackupPlanExecutionOperations",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeginsLogChainState",
                table: "BackupPlanExecutionOperations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "HasBulkLoggedData",
                table: "BackupPlanExecutionOperations",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HasBulkLoggedDataState",
                table: "BackupPlanExecutionOperations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "IsForceOffline",
                table: "BackupPlanExecutionOperations",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsForceOfflineState",
                table: "BackupPlanExecutionOperations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "RecoveryModel",
                table: "BackupPlanExecutionOperations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryModelState",
                table: "BackupPlanExecutionOperations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "BeginsLogChain",
                table: "BackupPlanExecutionObservations",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeginsLogChainState",
                table: "BackupPlanExecutionObservations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "HasBulkLoggedData",
                table: "BackupPlanExecutionObservations",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HasBulkLoggedDataState",
                table: "BackupPlanExecutionObservations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "IsForceOffline",
                table: "BackupPlanExecutionObservations",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsForceOfflineState",
                table: "BackupPlanExecutionObservations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "RecoveryModel",
                table: "BackupPlanExecutionObservations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryModelState",
                table: "BackupPlanExecutionObservations",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSets_BeginsLogChain",
                table: "BackupSets",
                sql: "([BeginsLogChainState] = 'Known' AND [BeginsLogChain] IS NOT NULL) OR ([BeginsLogChainState] IN ('Unknown') AND [BeginsLogChain] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSets_HasBulkLoggedData",
                table: "BackupSets",
                sql: "([HasBulkLoggedDataState] = 'Known' AND [HasBulkLoggedData] IS NOT NULL) OR ([HasBulkLoggedDataState] IN ('Unknown') AND [HasBulkLoggedData] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSets_IsForceOffline",
                table: "BackupSets",
                sql: "([IsForceOfflineState] = 'Known' AND [IsForceOffline] IS NOT NULL) OR ([IsForceOfflineState] IN ('Unknown') AND [IsForceOffline] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSets_RecoveryModel",
                table: "BackupSets",
                sql: "([RecoveryModelState] = 'Known' AND [RecoveryModel] IS NOT NULL AND [RecoveryModel] IN ('Full','BulkLogged','Simple')) OR ([RecoveryModelState] IN ('Unknown') AND [RecoveryModel] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSetEvidence_BeginsLogChain",
                table: "BackupSetEvidence",
                sql: "([BeginsLogChainState] = 'Known' AND [BeginsLogChain] IS NOT NULL) OR ([BeginsLogChainState] IN ('Unknown') AND [BeginsLogChain] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSetEvidence_HasBulkLoggedData",
                table: "BackupSetEvidence",
                sql: "([HasBulkLoggedDataState] = 'Known' AND [HasBulkLoggedData] IS NOT NULL) OR ([HasBulkLoggedDataState] IN ('Unknown') AND [HasBulkLoggedData] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSetEvidence_IsForceOffline",
                table: "BackupSetEvidence",
                sql: "([IsForceOfflineState] = 'Known' AND [IsForceOffline] IS NOT NULL) OR ([IsForceOfflineState] IN ('Unknown') AND [IsForceOffline] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSetEvidence_RecoveryModel",
                table: "BackupSetEvidence",
                sql: "([RecoveryModelState] = 'Known' AND [RecoveryModel] IS NOT NULL AND [RecoveryModel] IN ('Full','BulkLogged','Simple')) OR ([RecoveryModelState] IN ('Unknown') AND [RecoveryModel] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupSetEvidence_SourceObservedAtUtc",
                table: "BackupSetEvidence",
                sql: "[SourceObservedAtUtc] IS NULL OR DATEPART(TZOFFSET,[SourceObservedAtUtc]) = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanExecutionOperations_BeginsLogChain",
                table: "BackupPlanExecutionOperations",
                sql: "([BeginsLogChainState] = 'Known' AND [BeginsLogChain] IS NOT NULL) OR ([BeginsLogChainState] IN ('Unknown') AND [BeginsLogChain] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanExecutionOperations_HasBulkLoggedData",
                table: "BackupPlanExecutionOperations",
                sql: "([HasBulkLoggedDataState] = 'Known' AND [HasBulkLoggedData] IS NOT NULL) OR ([HasBulkLoggedDataState] IN ('Unknown') AND [HasBulkLoggedData] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanExecutionOperations_IsForceOffline",
                table: "BackupPlanExecutionOperations",
                sql: "([IsForceOfflineState] = 'Known' AND [IsForceOffline] IS NOT NULL) OR ([IsForceOfflineState] IN ('Unknown') AND [IsForceOffline] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanExecutionOperations_RecoveryModel",
                table: "BackupPlanExecutionOperations",
                sql: "([RecoveryModelState] = 'Known' AND [RecoveryModel] IS NOT NULL AND [RecoveryModel] IN ('Full','BulkLogged','Simple')) OR ([RecoveryModelState] IN ('Unknown') AND [RecoveryModel] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanExecutionObservations_BeginsLogChain",
                table: "BackupPlanExecutionObservations",
                sql: "([BeginsLogChainState] = 'Known' AND [BeginsLogChain] IS NOT NULL) OR ([BeginsLogChainState] IN ('Unknown') AND [BeginsLogChain] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanExecutionObservations_HasBulkLoggedData",
                table: "BackupPlanExecutionObservations",
                sql: "([HasBulkLoggedDataState] = 'Known' AND [HasBulkLoggedData] IS NOT NULL) OR ([HasBulkLoggedDataState] IN ('Unknown') AND [HasBulkLoggedData] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanExecutionObservations_IsForceOffline",
                table: "BackupPlanExecutionObservations",
                sql: "([IsForceOfflineState] = 'Known' AND [IsForceOffline] IS NOT NULL) OR ([IsForceOfflineState] IN ('Unknown') AND [IsForceOffline] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupPlanExecutionObservations_RecoveryModel",
                table: "BackupPlanExecutionObservations",
                sql: "([RecoveryModelState] = 'Known' AND [RecoveryModel] IS NOT NULL AND [RecoveryModel] IN ('Full','BulkLogged','Simple')) OR ([RecoveryModelState] IN ('Unknown') AND [RecoveryModel] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 新字段一旦承载实际 LOG 事实或来源时刻，回退会丢失证据；已有数据时拒绝。
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [BackupSets])
                OR EXISTS (SELECT 1 FROM [BackupSetEvidence])
                OR EXISTS (SELECT 1 FROM [BackupPlanExecutionOperations])
                OR EXISTS (SELECT 1 FROM [BackupPlanExecutionObservations])
                OR EXISTS (SELECT 1 FROM [BackupTasks])
                OR EXISTS (SELECT 1 FROM [BackupAttempts])
                OR EXISTS (
                    SELECT 1 FROM [BackupSets]
                    WHERE [RecoveryModelState] <> 'Unknown' OR [RecoveryModel] IS NOT NULL
                        OR [BeginsLogChainState] <> 'Unknown' OR [BeginsLogChain] IS NOT NULL
                        OR [HasBulkLoggedDataState] <> 'Unknown' OR [HasBulkLoggedData] IS NOT NULL
                        OR [IsForceOfflineState] <> 'Unknown' OR [IsForceOffline] IS NOT NULL)
                OR EXISTS (
                    SELECT 1 FROM [BackupSetEvidence]
                    WHERE [RecoveryModelState] <> 'Unknown' OR [RecoveryModel] IS NOT NULL
                        OR [BeginsLogChainState] <> 'Unknown' OR [BeginsLogChain] IS NOT NULL
                        OR [HasBulkLoggedDataState] <> 'Unknown' OR [HasBulkLoggedData] IS NOT NULL
                        OR [IsForceOfflineState] <> 'Unknown' OR [IsForceOffline] IS NOT NULL
                        OR [SourceObservedAtUtc] IS NOT NULL)
                OR EXISTS (
                    SELECT 1 FROM [BackupPlanExecutionOperations]
                    WHERE [RecoveryModelState] <> 'Unknown' OR [RecoveryModel] IS NOT NULL
                        OR [BeginsLogChainState] <> 'Unknown' OR [BeginsLogChain] IS NOT NULL
                        OR [HasBulkLoggedDataState] <> 'Unknown' OR [HasBulkLoggedData] IS NOT NULL
                        OR [IsForceOfflineState] <> 'Unknown' OR [IsForceOffline] IS NOT NULL)
                OR EXISTS (
                    SELECT 1 FROM [BackupPlanExecutionObservations]
                    WHERE [RecoveryModelState] <> 'Unknown' OR [RecoveryModel] IS NOT NULL
                        OR [BeginsLogChainState] <> 'Unknown' OR [BeginsLogChain] IS NOT NULL
                        OR [HasBulkLoggedDataState] <> 'Unknown' OR [HasBulkLoggedData] IS NOT NULL
                        OR [IsForceOfflineState] <> 'Unknown' OR [IsForceOffline] IS NOT NULL)
                BEGIN
                    THROW 51014, N'已有 LOG 实际元数据或来源观察时刻，不能回退该迁移。请保留现场并从迁移前平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSets_BeginsLogChain",
                table: "BackupSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSets_HasBulkLoggedData",
                table: "BackupSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSets_IsForceOffline",
                table: "BackupSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSets_RecoveryModel",
                table: "BackupSets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSetEvidence_BeginsLogChain",
                table: "BackupSetEvidence");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSetEvidence_HasBulkLoggedData",
                table: "BackupSetEvidence");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSetEvidence_IsForceOffline",
                table: "BackupSetEvidence");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSetEvidence_RecoveryModel",
                table: "BackupSetEvidence");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupSetEvidence_SourceObservedAtUtc",
                table: "BackupSetEvidence");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanExecutionOperations_BeginsLogChain",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanExecutionOperations_HasBulkLoggedData",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanExecutionOperations_IsForceOffline",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanExecutionOperations_RecoveryModel",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanExecutionObservations_BeginsLogChain",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanExecutionObservations_HasBulkLoggedData",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanExecutionObservations_IsForceOffline",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupPlanExecutionObservations_RecoveryModel",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropColumn(
                name: "BeginsLogChain",
                table: "BackupSets");

            migrationBuilder.DropColumn(
                name: "BeginsLogChainState",
                table: "BackupSets");

            migrationBuilder.DropColumn(
                name: "HasBulkLoggedData",
                table: "BackupSets");

            migrationBuilder.DropColumn(
                name: "HasBulkLoggedDataState",
                table: "BackupSets");

            migrationBuilder.DropColumn(
                name: "IsForceOffline",
                table: "BackupSets");

            migrationBuilder.DropColumn(
                name: "IsForceOfflineState",
                table: "BackupSets");

            migrationBuilder.DropColumn(
                name: "RecoveryModel",
                table: "BackupSets");

            migrationBuilder.DropColumn(
                name: "RecoveryModelState",
                table: "BackupSets");

            migrationBuilder.DropColumn(
                name: "BeginsLogChain",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "BeginsLogChainState",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "HasBulkLoggedData",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "HasBulkLoggedDataState",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "IsForceOffline",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "IsForceOfflineState",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "RecoveryModel",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "RecoveryModelState",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "SourceObservedAtUtc",
                table: "BackupSetEvidence");

            migrationBuilder.DropColumn(
                name: "BeginsLogChain",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropColumn(
                name: "BeginsLogChainState",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropColumn(
                name: "HasBulkLoggedData",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropColumn(
                name: "HasBulkLoggedDataState",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropColumn(
                name: "IsForceOffline",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropColumn(
                name: "IsForceOfflineState",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropColumn(
                name: "RecoveryModel",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropColumn(
                name: "RecoveryModelState",
                table: "BackupPlanExecutionOperations");

            migrationBuilder.DropColumn(
                name: "BeginsLogChain",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropColumn(
                name: "BeginsLogChainState",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropColumn(
                name: "HasBulkLoggedData",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropColumn(
                name: "HasBulkLoggedDataState",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropColumn(
                name: "IsForceOffline",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropColumn(
                name: "IsForceOfflineState",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropColumn(
                name: "RecoveryModel",
                table: "BackupPlanExecutionObservations");

            migrationBuilder.DropColumn(
                name: "RecoveryModelState",
                table: "BackupPlanExecutionObservations");
        }
    }
}
