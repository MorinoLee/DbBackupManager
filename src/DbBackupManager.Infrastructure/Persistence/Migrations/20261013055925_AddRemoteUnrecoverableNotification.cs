using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteUnrecoverableNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_NotificationOutbox_Identity",
                table: "NotificationOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NotificationOutbox_Type",
                table: "NotificationOutbox");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationOutbox_Identity",
                table: "NotificationOutbox",
                sql: "([Type] IN ('TaskFailed', 'TaskIndeterminate', 'RemoteUnrecoverable') AND [SourceKind] = 'BackupTask' AND [TaskId] = [SourceId] AND [Stage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')) OR ([Type] IN ('RetentionDeleteFailed', 'RetentionMissing') AND [SourceKind] = 'BackupFile' AND [TaskId] IS NOT NULL AND [Stage] IS NULL) OR ([Type] = 'AdminTest' AND [SourceKind] = 'AdminRequest' AND [TaskId] IS NULL AND [Stage] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationOutbox_Type",
                table: "NotificationOutbox",
                sql: "[Type] IN ('TaskFailed', 'TaskIndeterminate', 'RetentionDeleteFailed', 'RetentionMissing', 'AdminTest', 'RemoteUnrecoverable')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [NotificationOutbox] WHERE [Type] = N'RemoteUnrecoverable')
                BEGIN
                    THROW 51005, N'已有远端暂时无法恢复通知，不能回退。请从迁移前的平台库备份恢复。', 1;
                END
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_NotificationOutbox_Identity",
                table: "NotificationOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NotificationOutbox_Type",
                table: "NotificationOutbox");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationOutbox_Identity",
                table: "NotificationOutbox",
                sql: "([Type] IN ('TaskFailed', 'TaskIndeterminate') AND [SourceKind] = 'BackupTask' AND [TaskId] = [SourceId] AND [Stage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')) OR ([Type] IN ('RetentionDeleteFailed', 'RetentionMissing') AND [SourceKind] = 'BackupFile' AND [TaskId] IS NOT NULL AND [Stage] IS NULL) OR ([Type] = 'AdminTest' AND [SourceKind] = 'AdminRequest' AND [TaskId] IS NULL AND [Stage] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationOutbox_Type",
                table: "NotificationOutbox",
                sql: "[Type] IN ('TaskFailed', 'TaskIndeterminate', 'RetentionDeleteFailed', 'RetentionMissing', 'AdminTest')");
        }
    }
}
