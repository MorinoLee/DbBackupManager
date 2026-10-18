using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClosedUnknownTaskStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 收紧恢复终止身份前先核对既有记录；不完整身份不能被当作可解除的终止证据。
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [BackupInvocationAuthorizations]
                    WHERE [TerminationKind] = N'RecoveredTerminated'
                        AND ([BindingState] <> N'Known' OR [ConnectionId] IS NULL))
                BEGIN
                    THROW 51013, N'已有缺少完整原连接身份的恢复终止记录，不能升级。请核对现场终止证据，不能把它当成已解除。', 1;
                END
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTaskStateChanges_FromState",
                table: "BackupTaskStateChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTaskStateChanges_ToState",
                table: "BackupTaskStateChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_CompletedAt",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_Error",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_StartedAt",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_Status",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupInvocationAuthorizations_Termination",
                table: "BackupInvocationAuthorizations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTaskStateChanges_FromState",
                table: "BackupTaskStateChanges",
                sql: "([FromStatus] IS NULL AND [FromStage] IS NULL) OR ([FromStatus] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled', 'ClosedUnknown') AND ([FromStatus] = 'Cancelled' OR [FromStage] IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTaskStateChanges_ToState",
                table: "BackupTaskStateChanges",
                sql: "[ToStatus] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled', 'ClosedUnknown') AND ([ToStatus] = 'Cancelled' OR [ToStage] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_CompletedAt",
                table: "BackupTasks",
                sql: "([Status] IN ('Succeeded', 'Failed', 'Cancelled', 'ClosedUnknown') AND [CompletedAtUtc] IS NOT NULL) OR ([Status] NOT IN ('Succeeded', 'Failed', 'Cancelled', 'ClosedUnknown') AND [CompletedAtUtc] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_Error",
                table: "BackupTasks",
                sql: "([Status] IN ('Failed', 'NeedsAttention', 'ClosedUnknown') AND LEN([ErrorCode]) > 0 AND LEN([ErrorMessage]) > 0) OR ([Status] NOT IN ('Failed', 'NeedsAttention', 'ClosedUnknown') AND [ErrorCode] IS NULL AND [ErrorMessage] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_StartedAt",
                table: "BackupTasks",
                sql: "[Status] NOT IN ('Running', 'NeedsAttention', 'Succeeded', 'Failed', 'ClosedUnknown') OR [StartedAtUtc] IS NOT NULL OR ([Status] = 'Failed' AND [CurrentStage] = 'Backup' AND [CurrentBackupAttemptId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_Status",
                table: "BackupTasks",
                sql: "[Status] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled', 'ClosedUnknown')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupInvocationAuthorizations_Termination",
                table: "BackupInvocationAuthorizations",
                sql: "([TerminalObservedAtUtc] IS NULL AND [TerminationKind] IS NULL AND [TerminationEvidenceId] IS NULL AND [TerminationMutationId] IS NULL) OR ([TerminalObservedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[TerminalObservedAtUtc]) = 0 AND [TerminationKind] IS NOT NULL AND [TerminationKind] IN ('PlatformCompleted','PlatformConfirmedFailed','RecoveredTerminated') AND ([TerminationKind] <> 'RecoveredTerminated' OR ([BindingState] = 'Known' AND [ConnectionId] IS NOT NULL)) AND [TerminationEvidenceId] IS NOT NULL AND [TerminationEvidenceId] <> '00000000-0000-0000-0000-000000000000' AND [TerminationMutationId] IS NOT NULL AND [TerminationMutationId] <> '00000000-0000-0000-0000-000000000000')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回退会删除 ClosedUnknown 的合法取值；已有现场任务时必须保留原库，不做静默转换。
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [BackupTasks] WHERE [Status] = N'ClosedUnknown')
                BEGIN
                    THROW 51012, N'已有结果未知终态任务，不能回退 ClosedUnknown 状态约束。请保留现场并从迁移前的平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTaskStateChanges_FromState",
                table: "BackupTaskStateChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTaskStateChanges_ToState",
                table: "BackupTaskStateChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_CompletedAt",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_Error",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_StartedAt",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupTasks_Status",
                table: "BackupTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackupInvocationAuthorizations_Termination",
                table: "BackupInvocationAuthorizations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTaskStateChanges_FromState",
                table: "BackupTaskStateChanges",
                sql: "([FromStatus] IS NULL AND [FromStage] IS NULL) OR ([FromStatus] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled') AND ([FromStatus] = 'Cancelled' OR [FromStage] IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTaskStateChanges_ToState",
                table: "BackupTaskStateChanges",
                sql: "[ToStatus] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled') AND ([ToStatus] = 'Cancelled' OR [ToStage] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_CompletedAt",
                table: "BackupTasks",
                sql: "([Status] IN ('Succeeded', 'Failed', 'Cancelled') AND [CompletedAtUtc] IS NOT NULL) OR ([Status] NOT IN ('Succeeded', 'Failed', 'Cancelled') AND [CompletedAtUtc] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_Error",
                table: "BackupTasks",
                sql: "([Status] IN ('Failed', 'NeedsAttention') AND LEN([ErrorCode]) > 0 AND LEN([ErrorMessage]) > 0) OR ([Status] NOT IN ('Failed', 'NeedsAttention') AND [ErrorCode] IS NULL AND [ErrorMessage] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_StartedAt",
                table: "BackupTasks",
                sql: "[Status] NOT IN ('Running', 'NeedsAttention', 'Succeeded', 'Failed') OR [StartedAtUtc] IS NOT NULL OR ([Status] = 'Failed' AND [CurrentStage] = 'Backup' AND [CurrentBackupAttemptId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupTasks_Status",
                table: "BackupTasks",
                sql: "[Status] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackupInvocationAuthorizations_Termination",
                table: "BackupInvocationAuthorizations",
                sql: "([TerminalObservedAtUtc] IS NULL AND [TerminationKind] IS NULL AND [TerminationEvidenceId] IS NULL AND [TerminationMutationId] IS NULL) OR ([TerminalObservedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[TerminalObservedAtUtc]) = 0 AND [TerminationKind] IS NOT NULL AND [TerminationKind] IN ('PlatformCompleted','PlatformConfirmedFailed','RecoveredTerminated') AND ([TerminationKind] <> 'RecoveredTerminated' OR [BindingState] = 'Known') AND [TerminationEvidenceId] IS NOT NULL AND [TerminationEvidenceId] <> '00000000-0000-0000-0000-000000000000' AND [TerminationMutationId] IS NOT NULL AND [TerminationMutationId] <> '00000000-0000-0000-0000-000000000000')");
        }
    }
}
