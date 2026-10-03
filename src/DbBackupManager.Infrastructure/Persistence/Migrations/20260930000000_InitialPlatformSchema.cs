using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbBackupManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPlatformSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 150;",
                suppressTransaction: true);

            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SecurityStamp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    FailedLoginWindowStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    LockoutEndUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                    table.CheckConstraint("CK_AdminUsers_NormalizedUsername_NotEmpty", "LEN([NormalizedUsername]) > 0");
                    table.CheckConstraint("CK_AdminUsers_Username_NotEmpty", "LEN([Username]) > 0");
                });

            migrationBuilder.CreateTable(
                name: "CredentialReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(max)", maxLength: 65535, nullable: false),
                    ProtectedSecondarySecret = table.Column<string>(type: "nvarchar(max)", maxLength: 65535, nullable: true),
                    ProtectionVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredentialReferences", x => x.Id);
                    table.CheckConstraint("CK_CredentialReferences_Kind", "[Kind] IN ('SqlPassword', 'SmbPassword', 'SftpPassword', 'SftpPrivateKey', 'SmtpPassword')");
                    table.CheckConstraint("CK_CredentialReferences_Name_NotEmpty", "LEN([Name]) > 0");
                    table.CheckConstraint("CK_CredentialReferences_NormalizedName_NotEmpty", "LEN([NormalizedName]) > 0");
                    table.CheckConstraint("CK_CredentialReferences_ProtectedSecret_NotEmpty", "LEN([ProtectedSecret]) > 0");
                    table.CheckConstraint("CK_CredentialReferences_ProtectionVersion_NotEmpty", "LEN([ProtectionVersion]) > 0");
                    table.CheckConstraint("CK_CredentialReferences_SecondarySecret", "[Kind] = 'SftpPrivateKey' OR [ProtectedSecondarySecret] IS NULL");
                    table.CheckConstraint("CK_CredentialReferences_Username_NotEmpty", "LEN([Username]) > 0");
                });

            migrationBuilder.CreateTable(
                name: "NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MutationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SendLeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SendLeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SendLeaseAcquiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    SendLeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    SendAttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    LastFailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
                    table.CheckConstraint("CK_NotificationOutbox_ErrorCode_NotEmpty", "LEN([ErrorCode]) > 0");
                    table.CheckConstraint("CK_NotificationOutbox_Identity", "([Type] IN ('TaskFailed', 'TaskIndeterminate') AND [SourceKind] = 'BackupTask' AND [TaskId] = [SourceId] AND [Stage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')) OR ([Type] IN ('RetentionDeleteFailed', 'RetentionMissing') AND [SourceKind] = 'BackupFile' AND [TaskId] IS NOT NULL AND [Stage] IS NULL) OR ([Type] = 'AdminTest' AND [SourceKind] = 'AdminRequest' AND [TaskId] IS NULL AND [Stage] IS NULL)");
                    table.CheckConstraint("CK_NotificationOutbox_Outcome", "([Status] = 'Pending' AND [LastFailureCode] IS NULL AND [NextAttemptAtUtc] IS NULL AND [SentAtUtc] IS NULL) OR ([Status] = 'Sending' AND [LastFailureCode] IS NULL) OR ([Status] = 'SendFailed' AND LEN([LastFailureCode]) > 0 AND [NextAttemptAtUtc] IS NOT NULL AND [SentAtUtc] IS NULL AND [SendAttemptCount] >= 1) OR ([Status] = 'Sent' AND [LastFailureCode] IS NULL AND [NextAttemptAtUtc] IS NULL AND [SentAtUtc] >= [OccurredAtUtc] AND [SendAttemptCount] >= 1) OR ([Status] = 'Discarded' AND LEN([LastFailureCode]) > 0 AND [NextAttemptAtUtc] IS NULL AND [SentAtUtc] IS NULL)");
                    table.CheckConstraint("CK_NotificationOutbox_SendAttemptCount", "[SendAttemptCount] >= 0");
                    table.CheckConstraint("CK_NotificationOutbox_SendLease", "([Status] = 'Sending' AND [SendLeaseToken] IS NOT NULL AND LEN([SendLeaseOwner]) > 0 AND [SendLeaseAcquiredAtUtc] IS NOT NULL AND [SendLeaseAcquiredAtUtc] >= [OccurredAtUtc] AND [SendLeaseExpiresAtUtc] > [SendLeaseAcquiredAtUtc] AND [SendAttemptCount] >= 1 AND [NextAttemptAtUtc] IS NULL AND [SentAtUtc] IS NULL) OR ([Status] <> 'Sending' AND [SendLeaseToken] IS NULL AND [SendLeaseOwner] IS NULL AND [SendLeaseAcquiredAtUtc] IS NULL AND [SendLeaseExpiresAtUtc] IS NULL)");
                    table.CheckConstraint("CK_NotificationOutbox_SourceKind", "[SourceKind] IN ('BackupTask', 'BackupFile', 'AdminRequest')");
                    table.CheckConstraint("CK_NotificationOutbox_Status", "[Status] IN ('Pending', 'Sending', 'Sent', 'SendFailed', 'Discarded')");
                    table.CheckConstraint("CK_NotificationOutbox_Type", "[Type] IN ('TaskFailed', 'TaskIndeterminate', 'RetentionDeleteFailed', 'RetentionMissing', 'AdminTest')");
                });

            migrationBuilder.CreateTable(
                name: "WorkerHeartbeats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsStopped = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHeartbeats", x => x.Id);
                    table.CheckConstraint("CK_WorkerHeartbeats_SingleWorker", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "AuditRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorAdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Id);
                    table.CheckConstraint("CK_AuditRecords_Action_NotEmpty", "LEN([Action]) > 0");
                    table.CheckConstraint("CK_AuditRecords_Result_NotEmpty", "LEN([Result]) > 0");
                    table.CheckConstraint("CK_AuditRecords_TargetType_NotEmpty", "LEN([TargetType]) > 0");
                    table.ForeignKey(
                        name: "FK_AuditRecords_AdminUsers_ActorAdminUserId",
                        column: x => x.ActorAdminUserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LocalBackupRootPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    StagingAccessProtocol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    StagingAccessHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    StagingAccessPort = table.Column<int>(type: "int", nullable: true),
                    StagingAccessBasePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    StagingCredentialReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StagingSftpHostKeyFingerprint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseServers", x => x.Id);
                    table.CheckConstraint("CK_DatabaseServers_LocalBackupRootPath_NotEmpty", "LEN([LocalBackupRootPath]) > 0");
                    table.CheckConstraint("CK_DatabaseServers_Name_NotEmpty", "LEN([Name]) > 0");
                    table.CheckConstraint("CK_DatabaseServers_NormalizedName_NotEmpty", "LEN([NormalizedName]) > 0");
                    table.CheckConstraint("CK_DatabaseServers_StagingAccess", "([StagingAccessProtocol] = 'Smb' AND [StagingAccessPort] IS NULL AND [StagingSftpHostKeyFingerprint] IS NULL) OR ([StagingAccessProtocol] = 'Sftp' AND [StagingAccessPort] BETWEEN 1 AND 65535 AND LEN([StagingSftpHostKeyFingerprint]) > 0)");
                    table.CheckConstraint("CK_DatabaseServers_StagingBasePath_NotEmpty", "LEN([StagingAccessBasePath]) > 0");
                    table.CheckConstraint("CK_DatabaseServers_StagingHost_NotEmpty", "LEN([StagingAccessHost]) > 0");
                    table.ForeignKey(
                        name: "FK_DatabaseServers_CredentialReferences_StagingCredentialReferenceId",
                        column: x => x.StagingCredentialReferenceId,
                        principalTable: "CredentialReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SmtpSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    SecurityMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CredentialReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ConfigurationSerial = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmtpSettings", x => x.Id);
                    table.CheckConstraint("CK_SmtpSettings_ConfigurationSerial", "[ConfigurationSerial] >= 1");
                    table.CheckConstraint("CK_SmtpSettings_FromAddress_NotEmpty", "LEN([FromAddress]) > 0");
                    table.CheckConstraint("CK_SmtpSettings_Host_NotEmpty", "LEN([Host]) > 0");
                    table.CheckConstraint("CK_SmtpSettings_Port", "[Port] BETWEEN 1 AND 65535");
                    table.CheckConstraint("CK_SmtpSettings_SecurityMode", "[SecurityMode] IN ('StartTls', 'TlsOnConnect', 'Plaintext')");
                    table.CheckConstraint("CK_SmtpSettings_Singleton", "[Id] = '8F3C2A10-5D6E-4B91-9C7A-11D000000001'");
                    table.CheckConstraint("CK_SmtpSettings_Timeout", "[TimeoutSeconds] BETWEEN 1 AND 300");
                    table.ForeignKey(
                        name: "FK_SmtpSettings_CredentialReferences_CredentialReferenceId",
                        column: x => x.CredentialReferenceId,
                        principalTable: "CredentialReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StorageTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Protocol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: true),
                    BasePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    CredentialReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SftpHostKeyFingerprint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageTargets", x => x.Id);
                    table.CheckConstraint("CK_StorageTargets_BasePath_NotEmpty", "LEN([BasePath]) > 0");
                    table.CheckConstraint("CK_StorageTargets_Endpoint", "([Protocol] = 'Smb' AND [Port] IS NULL AND [SftpHostKeyFingerprint] IS NULL) OR ([Protocol] = 'Sftp' AND [Port] BETWEEN 1 AND 65535 AND LEN([SftpHostKeyFingerprint]) > 0)");
                    table.CheckConstraint("CK_StorageTargets_Host_NotEmpty", "LEN([Host]) > 0");
                    table.CheckConstraint("CK_StorageTargets_Name_NotEmpty", "LEN([Name]) > 0");
                    table.CheckConstraint("CK_StorageTargets_NormalizedName_NotEmpty", "LEN([NormalizedName]) > 0");
                    table.ForeignKey(
                        name: "FK_StorageTargets_CredentialReferences_CredentialReferenceId",
                        column: x => x.CredentialReferenceId,
                        principalTable: "CredentialReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ConnectionAddress = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    NormalizedConnectionAddress = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SqlCredentialReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncryptConnection = table.Column<bool>(type: "bit", nullable: false),
                    TrustServerCertificate = table.Column<bool>(type: "bit", nullable: false),
                    CertificateTrustReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowLegacyTls = table.Column<bool>(type: "bit", nullable: false),
                    LegacyTlsReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConnectionTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    ConnectionStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProductVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProductLevel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Edition = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LastConnectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    LastConnectionCheckedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    LastConnectionErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseInstances", x => x.Id);
                    table.CheckConstraint("CK_DatabaseInstances_CertificateTrustReason", "([TrustServerCertificate] = CAST(1 AS bit) AND LEN([CertificateTrustReason]) > 0) OR ([TrustServerCertificate] = CAST(0 AS bit) AND [CertificateTrustReason] IS NULL)");
                    table.CheckConstraint("CK_DatabaseInstances_ConnectionAddress_NotEmpty", "LEN([ConnectionAddress]) > 0 AND LEN([NormalizedConnectionAddress]) > 0");
                    table.CheckConstraint("CK_DatabaseInstances_ConnectionStatus", "[ConnectionStatus] IN ('Unknown', 'Connected', 'Failed')");
                    table.CheckConstraint("CK_DatabaseInstances_ConnectionTimeoutSeconds", "[ConnectionTimeoutSeconds] BETWEEN 1 AND 300");
                    table.CheckConstraint("CK_DatabaseInstances_EncryptConnection", "[EncryptConnection] = CAST(1 AS bit)");
                    table.CheckConstraint("CK_DatabaseInstances_LegacyTls", "([AllowLegacyTls] = 1 AND [LegacyTlsReason] IS NOT NULL AND LEN(LTRIM(RTRIM([LegacyTlsReason]))) > 0) OR ([AllowLegacyTls] = 0 AND [LegacyTlsReason] IS NULL)");
                    table.CheckConstraint("CK_DatabaseInstances_Name_NotEmpty", "LEN([Name]) > 0");
                    table.CheckConstraint("CK_DatabaseInstances_NormalizedName_NotEmpty", "LEN([NormalizedName]) > 0");
                    table.ForeignKey(
                        name: "FK_DatabaseInstances_CredentialReferences_SqlCredentialReferenceId",
                        column: x => x.SqlCredentialReferenceId,
                        principalTable: "CredentialReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatabaseInstances_DatabaseServers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "DatabaseServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SmtpRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SmtpSettingsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    NormalizedAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmtpRecipients", x => x.Id);
                    table.CheckConstraint("CK_SmtpRecipients_Address_NotEmpty", "LEN([Address]) > 0");
                    table.CheckConstraint("CK_SmtpRecipients_NormalizedAddress_NotEmpty", "LEN([NormalizedAddress]) > 0");
                    table.ForeignKey(
                        name: "FK_SmtpRecipients_SmtpSettings_SmtpSettingsId",
                        column: x => x.SmtpSettingsId,
                        principalTable: "SmtpSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManagedDatabases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NormalizedDatabaseName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RecoveryModel = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    StateDescription = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    IsSystemDatabase = table.Column<bool>(type: "bit", nullable: false),
                    IsManaged = table.Column<bool>(type: "bit", nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    LastDiscoveredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedDatabases", x => x.Id);
                    table.CheckConstraint("CK_ManagedDatabases_DatabaseName_NotEmpty", "LEN([DatabaseName]) > 0 AND LEN([NormalizedDatabaseName]) > 0");
                    table.CheckConstraint("CK_ManagedDatabases_SystemDatabaseNotManaged", "[IsSystemDatabase] = CAST(0 AS bit) OR [IsManaged] = CAST(0 AS bit)");
                    table.ForeignKey(
                        name: "FK_ManagedDatabases_DatabaseInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "DatabaseInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BackupType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StorageMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScheduleType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LocalTime = table.Column<TimeOnly>(type: "time(0)", nullable: false),
                    DaysOfWeek = table.Column<int>(type: "int", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    LocalRetentionDays = table.Column<int>(type: "int", nullable: true),
                    RemoteRetentionDays = table.Column<int>(type: "int", nullable: true),
                    UseChecksum = table.Column<bool>(type: "bit", nullable: false),
                    UseCompression = table.Column<bool>(type: "bit", nullable: false),
                    UseCopyOnly = table.Column<bool>(type: "bit", nullable: false),
                    BackupTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    VerifyTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    TransferTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsManualOnly = table.Column<bool>(type: "bit", nullable: false),
                    ScheduleEffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "BackupAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    BackupInvocationStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PreparedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    BackupStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    BackupFinishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    LocalSqlFilePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    WorkerSourceFilePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RemoteStorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RemotePartialFilePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    RemoteFinalFilePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    SourceLengthBytes = table.Column<long>(type: "bigint", nullable: true),
                    LocalVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    RemoteValidatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    LocalCleanupCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    OutcomeCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LocalSqlFilePathHash = table.Column<byte[]>(type: "binary(32)", nullable: true, computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', [LocalSqlFilePath]))", stored: true),
                    RemoteFinalFilePathHash = table.Column<byte[]>(type: "binary(32)", nullable: true, computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', [RemoteFinalFilePath]))", stored: true),
                    RemotePartialFilePathHash = table.Column<byte[]>(type: "binary(32)", nullable: true, computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', [RemotePartialFilePath]))", stored: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupAttempts", x => x.Id);
                    table.UniqueConstraint("AK_BackupAttempts_TaskId_Id", x => new { x.TaskId, x.Id });
                    table.CheckConstraint("CK_BackupAttempts_AttemptNumber", "[AttemptNumber] > 0");
                    table.CheckConstraint("CK_BackupAttempts_EvidenceOrder", "([LocalVerifiedAtUtc] IS NULL OR [LocalVerifiedAtUtc] >= [BackupFinishedAtUtc]) AND ([RemoteValidatedAtUtc] IS NULL OR ([SourceLengthBytes] > 0 AND [RemoteStorageTargetId] IS NOT NULL AND [RemoteValidatedAtUtc] >= [LocalVerifiedAtUtc])) AND ([LocalCleanupCompletedAtUtc] IS NULL OR ([RemoteValidatedAtUtc] IS NOT NULL AND [LocalCleanupCompletedAtUtc] >= [RemoteValidatedAtUtc]))");
                    table.CheckConstraint("CK_BackupAttempts_InvocationStatus", "[BackupInvocationStatus] IN ('Prepared', 'Running', 'Succeeded', 'ConfirmedFailed', 'Indeterminate')");
                    table.CheckConstraint("CK_BackupAttempts_InvocationTimes", "([BackupInvocationStatus] = 'Prepared' AND [BackupStartedAtUtc] IS NULL AND [BackupFinishedAtUtc] IS NULL) OR ([BackupInvocationStatus] = 'Running' AND [BackupStartedAtUtc] IS NOT NULL AND [BackupFinishedAtUtc] IS NULL) OR ([BackupInvocationStatus] IN ('Succeeded', 'ConfirmedFailed') AND [BackupStartedAtUtc] IS NOT NULL AND [BackupFinishedAtUtc] >= [BackupStartedAtUtc]) OR ([BackupInvocationStatus] = 'Indeterminate' AND [BackupStartedAtUtc] IS NOT NULL AND ([BackupFinishedAtUtc] IS NULL OR [BackupFinishedAtUtc] >= [BackupStartedAtUtc]))");
                    table.CheckConstraint("CK_BackupAttempts_LocalVerification", "([SourceLengthBytes] IS NULL AND [LocalVerifiedAtUtc] IS NULL) OR ([SourceLengthBytes] > 0 AND [LocalVerifiedAtUtc] IS NOT NULL AND [BackupInvocationStatus] = 'Succeeded')");
                    table.CheckConstraint("CK_BackupAttempts_OutcomeCode", "([BackupInvocationStatus] IN ('ConfirmedFailed', 'Indeterminate') AND LEN([OutcomeCode]) > 0) OR ([BackupInvocationStatus] NOT IN ('ConfirmedFailed', 'Indeterminate') AND [OutcomeCode] IS NULL)");
                    table.CheckConstraint("CK_BackupAttempts_RemotePaths", "([RemoteStorageTargetId] IS NULL AND [RemotePartialFilePath] IS NULL AND [RemoteFinalFilePath] IS NULL) OR ([RemoteStorageTargetId] IS NOT NULL AND LEN([RemotePartialFilePath]) > 0 AND LEN([RemoteFinalFilePath]) > 0 AND RIGHT([RemotePartialFilePath], 5) = '.part' AND [RemotePartialFilePath] <> [RemoteFinalFilePath])");
                    table.CheckConstraint("CK_BackupAttempts_RequiredPaths", "LEN([LocalSqlFilePath]) > 0 AND LEN([WorkerSourceFilePath]) > 0");
                    table.ForeignKey(
                        name: "FK_BackupAttempts_StorageTargets_RemoteStorageTargetId",
                        column: x => x.RemoteStorageTargetId,
                        principalTable: "StorageTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScheduledSlotAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CurrentStage = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CurrentBackupAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    ReconciliationAttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextReconciliationAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LeasePurpose = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LeaseAcquiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTasks", x => x.Id);
                    table.CheckConstraint("CK_BackupTasks_CancelledRequest", "[Status] <> 'Cancelled' OR [CancellationRequestedAtUtc] IS NOT NULL");
                    table.CheckConstraint("CK_BackupTasks_CompletedAt", "([Status] IN ('Succeeded', 'Failed', 'Cancelled') AND [CompletedAtUtc] IS NOT NULL) OR ([Status] NOT IN ('Succeeded', 'Failed', 'Cancelled') AND [CompletedAtUtc] IS NULL)");
                    table.CheckConstraint("CK_BackupTasks_CurrentAttempt", "([Status] IN ('Pending', 'Failed') AND [CurrentStage] = 'Backup') OR ([Status] = 'Cancelled' AND ([CurrentStage] IS NULL OR [CurrentStage] = 'Backup')) OR [CurrentBackupAttemptId] IS NOT NULL");
                    table.CheckConstraint("CK_BackupTasks_CurrentStage", "[CurrentStage] IS NULL OR [CurrentStage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')");
                    table.CheckConstraint("CK_BackupTasks_Error", "([Status] IN ('Failed', 'NeedsAttention') AND LEN([ErrorCode]) > 0 AND LEN([ErrorMessage]) > 0) OR ([Status] NOT IN ('Failed', 'NeedsAttention') AND [ErrorCode] IS NULL AND [ErrorMessage] IS NULL)");
                    table.CheckConstraint("CK_BackupTasks_LeaseCompleteness", "([LeasePurpose] IS NULL AND [LeaseToken] IS NULL AND [LeaseOwner] IS NULL AND [LeaseAcquiredAtUtc] IS NULL AND [LeaseExpiresAtUtc] IS NULL) OR ([LeasePurpose] IS NOT NULL AND [LeaseToken] IS NOT NULL AND LEN([LeaseOwner]) > 0 AND [LeaseAcquiredAtUtc] IS NOT NULL AND [LeaseExpiresAtUtc] > [LeaseAcquiredAtUtc])");
                    table.CheckConstraint("CK_BackupTasks_LeaseState", "([Status] = 'Running' AND [LeasePurpose] = 'Execution') OR ([Status] = 'NeedsAttention' AND ([LeasePurpose] IS NULL OR [LeasePurpose] = 'Reconciliation')) OR ([Status] NOT IN ('Running', 'NeedsAttention') AND [LeasePurpose] IS NULL)");
                    table.CheckConstraint("CK_BackupTasks_ReconciliationSchedule", "([Status] = 'NeedsAttention' AND [ReconciliationAttemptCount] >= 0 AND [NextReconciliationAtUtc] IS NOT NULL) OR ([Status] <> 'NeedsAttention' AND [ReconciliationAttemptCount] = 0 AND [NextReconciliationAtUtc] IS NULL)");
                    table.CheckConstraint("CK_BackupTasks_RetryCount", "[RetryCount] >= 0");
                    table.CheckConstraint("CK_BackupTasks_StartedAt", "[Status] NOT IN ('Running', 'NeedsAttention', 'Succeeded', 'Failed') OR [StartedAtUtc] IS NOT NULL OR ([Status] = 'Failed' AND [CurrentStage] = 'Backup' AND [CurrentBackupAttemptId] IS NULL)");
                    table.CheckConstraint("CK_BackupTasks_Status", "[Status] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled')");
                    table.CheckConstraint("CK_BackupTasks_StatusStage", "[Status] = 'Cancelled' OR [CurrentStage] IS NOT NULL");
                    table.CheckConstraint("CK_BackupTasks_Trigger", "([TriggerType] = 'Scheduled' AND [ScheduledSlotAtUtc] IS NOT NULL) OR ([TriggerType] = 'Manual' AND [ScheduledSlotAtUtc] IS NULL)");
                    table.ForeignKey(
                        name: "FK_BackupTasks_BackupAttempts_Id_CurrentBackupAttemptId",
                        columns: x => new { x.Id, x.CurrentBackupAttemptId },
                        principalTable: "BackupAttempts",
                        principalColumns: new[] { "TaskId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTasks_BackupPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "BackupPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DatabaseServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Protocol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    LengthBytes = table.Column<long>(type: "bigint", nullable: false),
                    ValidatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RetentionDays = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DeletionLeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionLeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DeletionLeaseAcquiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    DeletionLeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    DeletionAttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextDeletionAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    DeletionErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    MissingDetectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true),
                    PathHash = table.Column<byte[]>(type: "binary(32)", nullable: true, computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', [Path]))", stored: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupFiles", x => x.Id);
                    table.UniqueConstraint("AK_BackupFiles_TaskId_Id", x => new { x.TaskId, x.Id });
                    table.CheckConstraint("CK_BackupFiles_DeletionAttemptCount", "([Status] = 'Available' AND [DeletionAttemptCount] = 0) OR ([Status] <> 'Available' AND [DeletionAttemptCount] > 0)");
                    table.CheckConstraint("CK_BackupFiles_DeletionLease", "([Status] = 'DeletePending' AND [DeletionLeaseToken] IS NOT NULL AND LEN([DeletionLeaseOwner]) > 0 AND [DeletionLeaseAcquiredAtUtc] IS NOT NULL AND [DeletionLeaseAcquiredAtUtc] >= [ValidatedAtUtc] AND [DeletionLeaseExpiresAtUtc] > [DeletionLeaseAcquiredAtUtc]) OR ([Status] <> 'DeletePending' AND [DeletionLeaseToken] IS NULL AND [DeletionLeaseOwner] IS NULL AND [DeletionLeaseAcquiredAtUtc] IS NULL AND [DeletionLeaseExpiresAtUtc] IS NULL)");
                    table.CheckConstraint("CK_BackupFiles_DeletionOutcome", "([Status] IN ('Available', 'DeletePending') AND [NextDeletionAttemptAtUtc] IS NULL AND [DeletionErrorCode] IS NULL AND [DeletedAtUtc] IS NULL AND [MissingDetectedAtUtc] IS NULL) OR ([Status] = 'DeleteFailed' AND [NextDeletionAttemptAtUtc] IS NOT NULL AND LEN([DeletionErrorCode]) > 0 AND [DeletedAtUtc] IS NULL AND [MissingDetectedAtUtc] IS NULL) OR ([Status] = 'Deleted' AND [NextDeletionAttemptAtUtc] IS NULL AND [DeletionErrorCode] IS NULL AND [DeletedAtUtc] >= [ValidatedAtUtc] AND [MissingDetectedAtUtc] IS NULL) OR ([Status] = 'Missing' AND [NextDeletionAttemptAtUtc] IS NULL AND [DeletionErrorCode] IS NULL AND [DeletedAtUtc] IS NULL AND [MissingDetectedAtUtc] >= [ValidatedAtUtc])");
                    table.CheckConstraint("CK_BackupFiles_Identity", "([Location] = 'Local' AND [DatabaseServerId] IS NOT NULL AND [StorageTargetId] IS NULL) OR ([Location] = 'Remote' AND [DatabaseServerId] IS NULL AND [StorageTargetId] IS NOT NULL)");
                    table.CheckConstraint("CK_BackupFiles_Length", "[LengthBytes] > 0");
                    table.CheckConstraint("CK_BackupFiles_Location", "[Location] IN ('Local', 'Remote')");
                    table.CheckConstraint("CK_BackupFiles_Path_NotEmpty", "LEN([Path]) > 0");
                    table.CheckConstraint("CK_BackupFiles_Protocol", "[Protocol] IN ('Smb', 'Sftp')");
                    table.CheckConstraint("CK_BackupFiles_Retention", "[RetentionDays] BETWEEN 1 AND 36500");
                    table.CheckConstraint("CK_BackupFiles_Status", "[Status] IN ('Available', 'DeletePending', 'DeleteFailed', 'Deleted', 'Missing')");
                    table.ForeignKey(
                        name: "FK_BackupFiles_BackupAttempts_TaskId_AttemptId",
                        columns: x => new { x.TaskId, x.AttemptId },
                        principalTable: "BackupAttempts",
                        principalColumns: new[] { "TaskId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupFiles_BackupTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "BackupTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupFiles_DatabaseServers_DatabaseServerId",
                        column: x => x.DatabaseServerId,
                        principalTable: "DatabaseServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupFiles_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupFiles_StorageTargets_StorageTargetId",
                        column: x => x.StorageTargetId,
                        principalTable: "StorageTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupTaskSnapshots",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConnectionAddress = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SqlCredentialReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncryptConnection = table.Column<bool>(type: "bit", nullable: false),
                    TrustServerCertificate = table.Column<bool>(type: "bit", nullable: false),
                    CertificateTrustReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowLegacyTls = table.Column<bool>(type: "bit", nullable: false),
                    LegacyTlsReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConnectionTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    BackupType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UseChecksum = table.Column<bool>(type: "bit", nullable: false),
                    UseCompression = table.Column<bool>(type: "bit", nullable: false),
                    UseCopyOnly = table.Column<bool>(type: "bit", nullable: false),
                    BackupTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    VerifyTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    TransferTimeoutMinutes = table.Column<int>(type: "int", nullable: false),
                    LocalSqlBackupRootPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    FileNameRuleVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceAccessProtocol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SourceAccessHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SourceAccessPort = table.Column<int>(type: "int", nullable: true),
                    SourceAccessBasePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    SourceCredentialReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSftpHostKeyFingerprint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StorageMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StorageTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RemoteProtocol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    RemoteHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    RemotePort = table.Column<int>(type: "int", nullable: true),
                    RemoteBasePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    RemoteCredentialReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RemoteSftpHostKeyFingerprint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LocalRetentionDays = table.Column<int>(type: "int", nullable: true),
                    RemoteRetentionDays = table.Column<int>(type: "int", nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTaskSnapshots", x => x.TaskId);
                    table.CheckConstraint("CK_BackupTaskSnapshots_BackupType", "[BackupType] = 'Full'");
                    table.CheckConstraint("CK_BackupTaskSnapshots_ConnectionSecurity", "[EncryptConnection] = CAST(1 AS bit) AND (([TrustServerCertificate] = CAST(1 AS bit) AND LEN([CertificateTrustReason]) > 0) OR ([TrustServerCertificate] = CAST(0 AS bit) AND [CertificateTrustReason] IS NULL))");
                    table.CheckConstraint("CK_BackupTaskSnapshots_LegacyTls", "([AllowLegacyTls] = 1 AND [LegacyTlsReason] IS NOT NULL AND LEN(LTRIM(RTRIM([LegacyTlsReason]))) > 0) OR ([AllowLegacyTls] = 0 AND [LegacyTlsReason] IS NULL)");
                    table.CheckConstraint("CK_BackupTaskSnapshots_RequiredText", "LEN([PolicyName]) > 0 AND LEN([ServerName]) > 0 AND LEN([InstanceName]) > 0 AND LEN([DatabaseName]) > 0 AND LEN([ConnectionAddress]) > 0 AND LEN([LocalSqlBackupRootPath]) > 0 AND LEN([FileNameRuleVersion]) > 0 AND LEN([SourceAccessHost]) > 0 AND LEN([SourceAccessBasePath]) > 0 AND LEN([TimeZoneId]) > 0");
                    table.CheckConstraint("CK_BackupTaskSnapshots_SourceAccess", "([SourceAccessProtocol] = 'Smb' AND [SourceAccessPort] IS NULL AND [SourceSftpHostKeyFingerprint] IS NULL) OR ([SourceAccessProtocol] = 'Sftp' AND [SourceAccessPort] BETWEEN 1 AND 65535 AND LEN([SourceSftpHostKeyFingerprint]) > 0)");
                    table.CheckConstraint("CK_BackupTaskSnapshots_Storage", "([StorageMode] = 'LocalOnly' AND [StorageTargetId] IS NULL AND [RemoteProtocol] IS NULL AND [RemoteHost] IS NULL AND [RemotePort] IS NULL AND [RemoteBasePath] IS NULL AND [RemoteCredentialReferenceId] IS NULL AND [RemoteSftpHostKeyFingerprint] IS NULL AND [LocalRetentionDays] BETWEEN 1 AND 36500 AND [RemoteRetentionDays] IS NULL) OR ([StorageMode] IN ('LocalAndRemote', 'RemoteOnly') AND [StorageTargetId] IS NOT NULL AND [RemoteProtocol] IS NOT NULL AND LEN([RemoteHost]) > 0 AND LEN([RemoteBasePath]) > 0 AND [RemoteCredentialReferenceId] IS NOT NULL AND (([RemoteProtocol] = 'Smb' AND [RemotePort] IS NULL AND [RemoteSftpHostKeyFingerprint] IS NULL) OR ([RemoteProtocol] = 'Sftp' AND [RemotePort] BETWEEN 1 AND 65535 AND LEN([RemoteSftpHostKeyFingerprint]) > 0)) AND (([StorageMode] = 'LocalAndRemote' AND [LocalRetentionDays] BETWEEN 1 AND 36500) OR ([StorageMode] = 'RemoteOnly' AND [LocalRetentionDays] IS NULL)) AND [RemoteRetentionDays] BETWEEN 1 AND 36500)");
                    table.CheckConstraint("CK_BackupTaskSnapshots_Timeouts", "[ConnectionTimeoutSeconds] BETWEEN 1 AND 300 AND [BackupTimeoutMinutes] BETWEEN 1 AND 1440 AND [VerifyTimeoutMinutes] BETWEEN 1 AND 1440 AND [TransferTimeoutMinutes] BETWEEN 1 AND 1440");
                    table.ForeignKey(
                        name: "FK_BackupTaskSnapshots_BackupTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "BackupTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTaskSnapshots_CredentialReferences_RemoteCredentialReferenceId",
                        column: x => x.RemoteCredentialReferenceId,
                        principalTable: "CredentialReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTaskSnapshots_CredentialReferences_SourceCredentialReferenceId",
                        column: x => x.SourceCredentialReferenceId,
                        principalTable: "CredentialReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTaskSnapshots_CredentialReferences_SqlCredentialReferenceId",
                        column: x => x.SqlCredentialReferenceId,
                        principalTable: "CredentialReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTaskSnapshots_DatabaseInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "DatabaseInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTaskSnapshots_DatabaseServers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "DatabaseServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTaskSnapshots_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTaskSnapshots_StorageTargets_StorageTargetId",
                        column: x => x.StorageTargetId,
                        principalTable: "StorageTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupTaskStateChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MutationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    FromStage = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ToStage = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    BackupAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTaskStateChanges", x => x.Id);
                    table.CheckConstraint("CK_BackupTaskStateChanges_FromState", "([FromStatus] IS NULL AND [FromStage] IS NULL) OR ([FromStatus] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled') AND ([FromStatus] = 'Cancelled' OR [FromStage] IS NOT NULL))");
                    table.CheckConstraint("CK_BackupTaskStateChanges_ReasonCode_NotEmpty", "LEN([ReasonCode]) > 0");
                    table.CheckConstraint("CK_BackupTaskStateChanges_Stages", "([FromStage] IS NULL OR [FromStage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')) AND ([ToStage] IS NULL OR [ToStage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup'))");
                    table.CheckConstraint("CK_BackupTaskStateChanges_ToState", "[ToStatus] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled') AND ([ToStatus] = 'Cancelled' OR [ToStage] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_BackupTaskStateChanges_BackupAttempts_TaskId_BackupAttemptId",
                        columns: x => new { x.TaskId, x.BackupAttemptId },
                        principalTable: "BackupAttempts",
                        principalColumns: new[] { "TaskId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupTaskStateChanges_BackupTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "BackupTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskEvents", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_TaskEvents_BackupTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "BackupTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupFileStateChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MutationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupFileStateChanges", x => x.Id);
                    table.CheckConstraint("CK_BackupFileStateChanges_FromStatus", "[FromStatus] IS NULL OR [FromStatus] IN ('Available', 'DeletePending', 'DeleteFailed', 'Deleted', 'Missing')");
                    table.CheckConstraint("CK_BackupFileStateChanges_InitialStatus", "[FromStatus] IS NOT NULL OR [ToStatus] = 'Available'");
                    table.CheckConstraint("CK_BackupFileStateChanges_ReasonCode_NotEmpty", "LEN([ReasonCode]) > 0");
                    table.CheckConstraint("CK_BackupFileStateChanges_ToStatus", "[ToStatus] IN ('Available', 'DeletePending', 'DeleteFailed', 'Deleted', 'Missing')");
                    table.ForeignKey(
                        name: "FK_BackupFileStateChanges_BackupFiles_TaskId_FileId",
                        columns: x => new { x.TaskId, x.FileId },
                        principalTable: "BackupFiles",
                        principalColumns: new[] { "TaskId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupFileStateChanges_BackupTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "BackupTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_AdminUsers_NormalizedUsername",
                table: "AdminUsers",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_ActorAdminUserId",
                table: "AuditRecords",
                column: "ActorAdminUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_OccurredAtUtc",
                table: "AuditRecords",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "UX_BackupAttempts_LocalSqlFilePathHash",
                table: "BackupAttempts",
                column: "LocalSqlFilePathHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BackupAttempts_RemoteFinalPath",
                table: "BackupAttempts",
                columns: new[] { "RemoteStorageTargetId", "RemoteFinalFilePathHash" },
                unique: true,
                filter: "[RemoteStorageTargetId] IS NOT NULL AND [RemoteFinalFilePath] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BackupAttempts_RemotePartialPath",
                table: "BackupAttempts",
                columns: new[] { "RemoteStorageTargetId", "RemotePartialFilePathHash" },
                unique: true,
                filter: "[RemoteStorageTargetId] IS NOT NULL AND [RemotePartialFilePath] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BackupAttempts_TaskId_AttemptNumber",
                table: "BackupAttempts",
                columns: new[] { "TaskId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_DatabaseId",
                table: "BackupFiles",
                column: "DatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_RetentionQueue",
                table: "BackupFiles",
                columns: new[] { "Status", "NextDeletionAttemptAtUtc", "DeletionLeaseExpiresAtUtc", "ValidatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupFiles_TaskId_AttemptId",
                table: "BackupFiles",
                columns: new[] { "TaskId", "AttemptId" });

            migrationBuilder.CreateIndex(
                name: "UX_BackupFiles_AttemptId_Location",
                table: "BackupFiles",
                columns: new[] { "AttemptId", "Location" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BackupFiles_DeletionLeaseToken",
                table: "BackupFiles",
                column: "DeletionLeaseToken",
                unique: true,
                filter: "[DeletionLeaseToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BackupFiles_LocalPath",
                table: "BackupFiles",
                columns: new[] { "DatabaseServerId", "Protocol", "PathHash" },
                unique: true,
                filter: "[Location] = 'Local'");

            migrationBuilder.CreateIndex(
                name: "UX_BackupFiles_RemotePath",
                table: "BackupFiles",
                columns: new[] { "StorageTargetId", "Protocol", "PathHash" },
                unique: true,
                filter: "[Location] = 'Remote'");

            migrationBuilder.CreateIndex(
                name: "UX_BackupFiles_TaskId_Location",
                table: "BackupFiles",
                columns: new[] { "TaskId", "Location" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupFileStateChanges_FileId_OccurredAtUtc",
                table: "BackupFileStateChanges",
                columns: new[] { "FileId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupFileStateChanges_TaskId_FileId",
                table: "BackupFileStateChanges",
                columns: new[] { "TaskId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupFileStateChanges_TaskId_OccurredAtUtc",
                table: "BackupFileStateChanges",
                columns: new[] { "TaskId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_BackupFileStateChanges_MutationId",
                table: "BackupFileStateChanges",
                column: "MutationId",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_BackupTasks_ExpiredLease",
                table: "BackupTasks",
                columns: new[] { "Status", "LeasePurpose", "LeaseExpiresAtUtc" },
                filter: "[LeaseExpiresAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTasks_Id_CurrentBackupAttemptId",
                table: "BackupTasks",
                columns: new[] { "Id", "CurrentBackupAttemptId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupTasks_Queue",
                table: "BackupTasks",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupTasks_ReconciliationQueue",
                table: "BackupTasks",
                columns: new[] { "Status", "NextReconciliationAtUtc", "LeasePurpose", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_BackupTasks_LeaseToken",
                table: "BackupTasks",
                column: "LeaseToken",
                unique: true,
                filter: "[LeaseToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BackupTasks_PolicyId_ScheduledSlotAtUtc",
                table: "BackupTasks",
                columns: new[] { "PolicyId", "ScheduledSlotAtUtc" },
                unique: true,
                filter: "[ScheduledSlotAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskSnapshots_DatabaseId",
                table: "BackupTaskSnapshots",
                column: "DatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskSnapshots_InstanceId",
                table: "BackupTaskSnapshots",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskSnapshots_RemoteCredentialReferenceId",
                table: "BackupTaskSnapshots",
                column: "RemoteCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskSnapshots_ServerId",
                table: "BackupTaskSnapshots",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskSnapshots_SourceCredentialReferenceId",
                table: "BackupTaskSnapshots",
                column: "SourceCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskSnapshots_SqlCredentialReferenceId",
                table: "BackupTaskSnapshots",
                column: "SqlCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskSnapshots_StorageTargetId",
                table: "BackupTaskSnapshots",
                column: "StorageTargetId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskStateChanges_TaskId_BackupAttemptId",
                table: "BackupTaskStateChanges",
                columns: new[] { "TaskId", "BackupAttemptId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupTaskStateChanges_TaskId_OccurredAtUtc",
                table: "BackupTaskStateChanges",
                columns: new[] { "TaskId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_BackupTaskStateChanges_MutationId",
                table: "BackupTaskStateChanges",
                column: "MutationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CredentialReferences_NormalizedName",
                table: "CredentialReferences",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseInstances_SqlCredentialReferenceId",
                table: "DatabaseInstances",
                column: "SqlCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "UX_DatabaseInstances_NormalizedConnectionAddress",
                table: "DatabaseInstances",
                column: "NormalizedConnectionAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DatabaseInstances_ServerId_NormalizedName",
                table: "DatabaseInstances",
                columns: new[] { "ServerId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseServers_StagingCredentialReferenceId",
                table: "DatabaseServers",
                column: "StagingCredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "UX_DatabaseServers_NormalizedName",
                table: "DatabaseServers",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ManagedDatabases_InstanceId_NormalizedDatabaseName",
                table: "ManagedDatabases",
                columns: new[] { "InstanceId", "NormalizedDatabaseName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Claim",
                table: "NotificationOutbox",
                columns: new[] { "Status", "NextAttemptAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_NotificationOutbox_MutationId_Type",
                table: "NotificationOutbox",
                columns: new[] { "MutationId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_NotificationOutbox_SendLeaseToken",
                table: "NotificationOutbox",
                column: "SendLeaseToken",
                unique: true,
                filter: "[SendLeaseToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SmtpRecipients_SmtpSettingsId",
                table: "SmtpRecipients",
                column: "SmtpSettingsId");

            migrationBuilder.CreateIndex(
                name: "UX_SmtpRecipients_NormalizedAddress",
                table: "SmtpRecipients",
                column: "NormalizedAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SmtpSettings_CredentialReferenceId",
                table: "SmtpSettings",
                column: "CredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageTargets_CredentialReferenceId",
                table: "StorageTargets",
                column: "CredentialReferenceId");

            migrationBuilder.CreateIndex(
                name: "UX_StorageTargets_NormalizedName",
                table: "StorageTargets",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskEvents_OccurredAtUtc_EventId",
                table: "TaskEvents",
                columns: new[] { "OccurredAtUtc", "EventId" },
                filter: "[PublishedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskEvents_Published_OccurredAtUtc_EventId",
                table: "TaskEvents",
                columns: new[] { "OccurredAtUtc", "EventId" },
                filter: "[PublishedAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskEvents_TaskId",
                table: "TaskEvents",
                column: "TaskId");

            migrationBuilder.AddForeignKey(
                name: "FK_BackupAttempts_BackupTasks_TaskId",
                table: "BackupAttempts",
                column: "TaskId",
                principalTable: "BackupTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackupAttempts_BackupTasks_TaskId",
                table: "BackupAttempts");

            migrationBuilder.DropTable(
                name: "AuditRecords");

            migrationBuilder.DropTable(
                name: "BackupFileStateChanges");

            migrationBuilder.DropTable(
                name: "BackupTaskSnapshots");

            migrationBuilder.DropTable(
                name: "BackupTaskStateChanges");

            migrationBuilder.DropTable(
                name: "NotificationOutbox");

            migrationBuilder.DropTable(
                name: "SmtpRecipients");

            migrationBuilder.DropTable(
                name: "TaskEvents");

            migrationBuilder.DropTable(
                name: "WorkerHeartbeats");

            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "BackupFiles");

            migrationBuilder.DropTable(
                name: "SmtpSettings");

            migrationBuilder.DropTable(
                name: "BackupTasks");

            migrationBuilder.DropTable(
                name: "BackupAttempts");

            migrationBuilder.DropTable(
                name: "BackupPolicies");

            migrationBuilder.DropTable(
                name: "ManagedDatabases");

            migrationBuilder.DropTable(
                name: "StorageTargets");

            migrationBuilder.DropTable(
                name: "DatabaseInstances");

            migrationBuilder.DropTable(
                name: "DatabaseServers");

            migrationBuilder.DropTable(
                name: "CredentialReferences");
        }
    }
}
