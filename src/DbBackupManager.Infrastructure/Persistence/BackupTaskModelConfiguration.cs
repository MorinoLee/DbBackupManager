using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskConfiguration : IEntityTypeConfiguration<BackupTask>
{
    public void Configure(EntityTypeBuilder<BackupTask> builder)
    {
        builder.ToTable("BackupTasks", table =>
        {
            table.HasCheckConstraint("CK_BackupTasks_Identity",
                "([PolicyId] IS NOT NULL AND [PlanId] IS NULL AND [PlanVersionId] IS NULL) OR "
                + "([PolicyId] IS NULL AND [PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL)");
            table.HasCheckConstraint("CK_BackupTasks_BackupType",
                "[BackupType] IS NOT NULL AND [BackupType] IN ('Full', 'Differential', 'Log') "
                + "AND ([PolicyId] IS NULL OR [BackupType] = 'Full')");
            table.HasCheckConstraint("CK_BackupTasks_CoveredDifferentialSlot",
                "[CoveredDifferentialSlotUtc] IS NULL OR ([PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL "
                + "AND [PolicyId] IS NULL AND [BackupType] IS NOT NULL AND [BackupType] = 'Full' "
                + "AND DATEPART(TZOFFSET, [CoveredDifferentialSlotUtc]) = 0)");
            table.HasCheckConstraint(
                "CK_BackupTasks_Trigger",
                "([TriggerType] = 'Scheduled' AND [ScheduledSlotAtUtc] IS NOT NULL) OR "
                + "([TriggerType] = 'Manual' AND [ScheduledSlotAtUtc] IS NULL)");
            table.HasCheckConstraint(
                "CK_BackupTasks_Status",
                "[Status] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled')");
            table.HasCheckConstraint(
                "CK_BackupTasks_CurrentStage",
                "[CurrentStage] IS NULL OR [CurrentStage] IN "
                + "('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')");
            table.HasCheckConstraint(
                "CK_BackupTasks_StatusStage",
                "[Status] = 'Cancelled' OR [CurrentStage] IS NOT NULL");
            table.HasCheckConstraint("CK_BackupTasks_RetryCount", "[RetryCount] >= 0");
            table.HasCheckConstraint(
                "CK_BackupTasks_ReconciliationSchedule",
                "([Status] = 'NeedsAttention' AND [ReconciliationAttemptCount] >= 0 "
                + "AND [NextReconciliationAtUtc] IS NOT NULL) OR "
                + "([Status] <> 'NeedsAttention' AND [ReconciliationAttemptCount] = 0 "
                + "AND [NextReconciliationAtUtc] IS NULL)");
            table.HasCheckConstraint(
                "CK_BackupTasks_CurrentAttempt",
                "([Status] IN ('Pending', 'Failed') AND [CurrentStage] = 'Backup') OR "
                + "([Status] = 'Cancelled' AND ([CurrentStage] IS NULL OR [CurrentStage] = 'Backup')) OR "
                + "[CurrentBackupAttemptId] IS NOT NULL");
            table.HasCheckConstraint(
                "CK_BackupTasks_Error",
                "([Status] IN ('Failed', 'NeedsAttention') AND LEN([ErrorCode]) > 0 "
                + "AND LEN([ErrorMessage]) > 0) OR "
                + "([Status] NOT IN ('Failed', 'NeedsAttention') AND [ErrorCode] IS NULL "
                + "AND [ErrorMessage] IS NULL)");
            table.HasCheckConstraint(
                "CK_BackupTasks_StartedAt",
                "[Status] NOT IN ('Running', 'NeedsAttention', 'Succeeded', 'Failed') "
                + "OR [StartedAtUtc] IS NOT NULL OR ([Status] = 'Failed' AND [CurrentStage] = 'Backup' "
                + "AND [CurrentBackupAttemptId] IS NULL)");
            table.HasCheckConstraint(
                "CK_BackupTasks_CompletedAt",
                "([Status] IN ('Succeeded', 'Failed', 'Cancelled') AND [CompletedAtUtc] IS NOT NULL) OR "
                + "([Status] NOT IN ('Succeeded', 'Failed', 'Cancelled') AND [CompletedAtUtc] IS NULL)");
            table.HasCheckConstraint(
                "CK_BackupTasks_CancelledRequest",
                "[Status] <> 'Cancelled' OR [CancellationRequestedAtUtc] IS NOT NULL");
            table.HasCheckConstraint(
                "CK_BackupTasks_LeaseCompleteness",
                "([LeasePurpose] IS NULL AND [LeaseToken] IS NULL AND [LeaseOwner] IS NULL "
                + "AND [LeaseAcquiredAtUtc] IS NULL AND [LeaseExpiresAtUtc] IS NULL) OR "
                + "([LeasePurpose] IS NOT NULL AND [LeaseToken] IS NOT NULL AND LEN([LeaseOwner]) > 0 "
                + "AND [LeaseAcquiredAtUtc] IS NOT NULL AND [LeaseExpiresAtUtc] > [LeaseAcquiredAtUtc])");
            table.HasCheckConstraint(
                "CK_BackupTasks_LeaseState",
                "([Status] = 'Running' AND [LeasePurpose] = 'Execution') OR "
                + "([Status] = 'NeedsAttention' AND ([LeasePurpose] IS NULL "
                + "OR [LeasePurpose] = 'Reconciliation')) OR "
                + "([Status] NOT IN ('Running', 'NeedsAttention') AND [LeasePurpose] IS NULL)");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.BackupType).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(BackupType.Full).HasSentinel(BackupType.Full).IsRequired();
        builder.Property(x => x.CoveredDifferentialSlotUtc).HasPrecision(7);
        builder.Property(x => x.TriggerType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.ScheduledSlotAtUtc).HasPrecision(7);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.CurrentStage).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.StartedAtUtc).HasPrecision(7);
        builder.Property(x => x.CompletedAtUtc).HasPrecision(7);
        builder.Property(x => x.CancellationRequestedAtUtc).HasPrecision(7);
        builder.Property(x => x.NextReconciliationAtUtc).HasPrecision(7);
        builder.Property(x => x.ErrorCode).HasMaxLength(100);
        builder.Property(x => x.ErrorMessage).HasMaxLength(500);
        builder.Property(x => x.LeasePurpose).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.LeaseOwner).HasMaxLength(200);
        builder.Property(x => x.LeaseAcquiredAtUtc).HasPrecision(7);
        builder.Property(x => x.LeaseExpiresAtUtc).HasPrecision(7);
        builder.HasIndex(x => new { x.PolicyId, x.ScheduledSlotAtUtc })
            .IsUnique()
            .HasFilter("[PolicyId] IS NOT NULL AND [ScheduledSlotAtUtc] IS NOT NULL")
            .HasDatabaseName("UX_BackupTasks_PolicyId_ScheduledSlotAtUtc");
        builder.HasIndex(x => new { x.PlanId, x.PlanVersionId, x.BackupType, x.ScheduledSlotAtUtc })
            .IsUnique()
            .HasFilter("[PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL "
                + "AND [BackupType] IS NOT NULL AND [ScheduledSlotAtUtc] IS NOT NULL")
            .HasDatabaseName("UX_BackupTasks_PlanVersion_Type_Slot");
        builder.HasIndex(x => x.LeaseToken)
            .IsUnique()
            .HasFilter("[LeaseToken] IS NOT NULL")
            .HasDatabaseName("UX_BackupTasks_LeaseToken");
        builder.HasIndex(x => new { x.Status, x.CreatedAtUtc, x.Id })
            .HasDatabaseName("IX_BackupTasks_Queue");
        builder.HasIndex(x => new { x.Status, x.LeasePurpose, x.LeaseExpiresAtUtc })
            .HasFilter("[LeaseExpiresAtUtc] IS NOT NULL")
            .HasDatabaseName("IX_BackupTasks_ExpiredLease");
        builder.HasIndex(x => new
        {
            x.Status,
            x.NextReconciliationAtUtc,
            x.LeasePurpose,
            x.LeaseExpiresAtUtc,
        })
            .HasDatabaseName("IX_BackupTasks_ReconciliationQueue");
        builder.HasOne<BackupPolicy>()
            .WithMany()
            .HasForeignKey(x => x.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupPlanVersion>()
            .WithMany()
            .HasForeignKey(x => new { x.PlanId, x.PlanVersionId })
            .HasPrincipalKey(x => new { x.PlanId, PlanVersionId = x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupAttempt>()
            .WithMany()
            .HasForeignKey(x => new { TaskId = x.Id, AttemptId = x.CurrentBackupAttemptId })
            .HasPrincipalKey(x => new { x.TaskId, AttemptId = x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupTaskSnapshotConfiguration
    : IEntityTypeConfiguration<BackupTaskSnapshot>
{
    public void Configure(EntityTypeBuilder<BackupTaskSnapshot> builder)
    {
        builder.ToTable("BackupTaskSnapshots", table =>
        {
            table.HasCheckConstraint(
                "CK_BackupTaskSnapshots_RequiredText",
                "LEN([PolicyName]) > 0 AND LEN([ServerName]) > 0 AND LEN([InstanceName]) > 0 "
                + "AND LEN([DatabaseName]) > 0 AND LEN([ConnectionAddress]) > 0 "
                + "AND LEN([LocalSqlBackupRootPath]) > 0 AND LEN([FileNameRuleVersion]) > 0 "
                + "AND LEN([SourceAccessHost]) > 0 AND LEN([SourceAccessBasePath]) > 0 "
                + "AND LEN([TimeZoneId]) > 0");
            table.HasCheckConstraint("CK_BackupTaskSnapshots_LegacyTls",
                "([AllowLegacyTls] = 1 AND [LegacyTlsReason] IS NOT NULL AND LEN(LTRIM(RTRIM([LegacyTlsReason]))) > 0) OR ([AllowLegacyTls] = 0 AND [LegacyTlsReason] IS NULL)");
            table.HasCheckConstraint(
                "CK_BackupTaskSnapshots_ConnectionSecurity",
                "[EncryptConnection] = CAST(1 AS bit) AND "
                + "(([TrustServerCertificate] = CAST(1 AS bit) AND LEN([CertificateTrustReason]) > 0) "
                + "OR ([TrustServerCertificate] = CAST(0 AS bit) AND [CertificateTrustReason] IS NULL))");
            table.HasCheckConstraint(
                "CK_BackupTaskSnapshots_SourceAccess",
                "([SourceAccessProtocol] = 'Smb' AND [SourceAccessPort] IS NULL "
                + "AND [SourceSftpHostKeyFingerprint] IS NULL) OR "
                + "([SourceAccessProtocol] = 'Sftp' AND [SourceAccessPort] BETWEEN 1 AND 65535 "
                + "AND LEN([SourceSftpHostKeyFingerprint]) > 0)");
            table.HasCheckConstraint(
                "CK_BackupTaskSnapshots_Storage",
                "([StorageMode] = 'LocalOnly' AND [StorageTargetId] IS NULL "
                + "AND [RemoteProtocol] IS NULL AND [RemoteHost] IS NULL AND [RemotePort] IS NULL "
                + "AND [RemoteBasePath] IS NULL AND [RemoteCredentialReferenceId] IS NULL "
                + "AND [RemoteSftpHostKeyFingerprint] IS NULL "
                + "AND [LocalRetentionDays] BETWEEN 1 AND 36500 AND [RemoteRetentionDays] IS NULL) OR "
                + "([StorageMode] IN ('LocalAndRemote', 'RemoteOnly') AND [StorageTargetId] IS NOT NULL "
                + "AND [RemoteProtocol] IS NOT NULL AND LEN([RemoteHost]) > 0 "
                + "AND LEN([RemoteBasePath]) > 0 AND [RemoteCredentialReferenceId] IS NOT NULL "
                + "AND (([RemoteProtocol] = 'Smb' AND [RemotePort] IS NULL "
                + "AND [RemoteSftpHostKeyFingerprint] IS NULL) OR "
                + "([RemoteProtocol] = 'Sftp' AND [RemotePort] BETWEEN 1 AND 65535 "
                + "AND LEN([RemoteSftpHostKeyFingerprint]) > 0)) "
                + "AND (([StorageMode] = 'LocalAndRemote' AND [LocalRetentionDays] BETWEEN 1 AND 36500) "
                + "OR ([StorageMode] = 'RemoteOnly' AND [LocalRetentionDays] IS NULL)) "
                + "AND [RemoteRetentionDays] BETWEEN 1 AND 36500)");
            table.HasCheckConstraint(
                "CK_BackupTaskSnapshots_BackupType",
                "[BackupType] IS NOT NULL AND [BackupType] IN ('Full', 'Differential', 'Log')");
            table.HasCheckConstraint("CK_BackupTaskSnapshots_Purpose",
                "([Purpose] IS NULL AND [BackupType] IS NOT NULL AND [BackupType] = 'Full') OR "
                + "([Purpose] IS NOT NULL AND [BackupType] IS NOT NULL AND [UseCopyOnly] IS NOT NULL AND "
                + "(([Purpose] = 'PlanFull' AND [BackupType] = 'Full' AND [UseCopyOnly] = 0) OR "
                + "([Purpose] = 'PlanDifferential' AND [BackupType] = 'Differential' AND [UseCopyOnly] = 0) OR "
                + "([Purpose] = 'PlanLog' AND [BackupType] = 'Log' AND [UseCopyOnly] = 0) OR "
                + "([Purpose] = 'AdHocCopyOnlyFull' AND [BackupType] = 'Full' AND [UseCopyOnly] = 1)))");
            table.HasCheckConstraint("CK_BackupTaskSnapshots_PlanPathVersion",
                "[Purpose] IS NULL OR ([Purpose] IS NOT NULL AND [FileNameRuleVersion] IS NOT NULL "
                + "AND [FileNameRuleVersion] = 'v3')");
            table.HasCheckConstraint(
                "CK_BackupTaskSnapshots_Timeouts",
                "[ConnectionTimeoutSeconds] BETWEEN 1 AND 300 "
                + "AND [BackupTimeoutMinutes] BETWEEN 1 AND 1440 "
                + "AND [VerifyTimeoutMinutes] BETWEEN 1 AND 1440 "
                + "AND [TransferTimeoutMinutes] BETWEEN 1 AND 1440");
        });
        builder.HasKey(x => x.TaskId);
        builder.Property(x => x.PolicyName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ServerName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.InstanceName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DatabaseName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ConnectionAddress).HasMaxLength(255).IsRequired();
        builder.Property(x => x.CertificateTrustReason).HasMaxLength(500);
        builder.Property(x => x.AllowLegacyTls).IsRequired();
        builder.Property(x => x.LegacyTlsReason).HasMaxLength(500);
        builder.Property(x => x.BackupType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.LocalSqlBackupRootPath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.FileNameRuleVersion).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SourceAccessProtocol)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();
        builder.Property(x => x.SourceAccessHost).HasMaxLength(255).IsRequired();
        builder.Property(x => x.SourceAccessBasePath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.SourceSftpHostKeyFingerprint).HasMaxLength(500);
        builder.Property(x => x.StorageMode).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.RemoteProtocol).HasConversion<string>().HasMaxLength(10);
        builder.Property(x => x.RemoteHost).HasMaxLength(255);
        builder.Property(x => x.RemoteBasePath).HasMaxLength(2048);
        builder.Property(x => x.RemoteSftpHostKeyFingerprint).HasMaxLength(500);
        builder.Property(x => x.TimeZoneId).HasMaxLength(150).IsRequired();
        builder.HasOne<BackupTask>()
            .WithOne()
            .HasForeignKey<BackupTaskSnapshot>(x => x.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DatabaseServer>()
            .WithMany()
            .HasForeignKey(x => x.ServerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DatabaseInstance>()
            .WithMany()
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ManagedDatabase>()
            .WithMany()
            .HasForeignKey(x => x.DatabaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CredentialReference>()
            .WithMany()
            .HasForeignKey(x => x.SqlCredentialReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CredentialReference>()
            .WithMany()
            .HasForeignKey(x => x.SourceCredentialReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StorageTarget>()
            .WithMany()
            .HasForeignKey(x => x.StorageTargetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CredentialReference>()
            .WithMany()
            .HasForeignKey(x => x.RemoteCredentialReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupAttemptConfiguration : IEntityTypeConfiguration<BackupAttempt>
{
    public void Configure(EntityTypeBuilder<BackupAttempt> builder)
    {
        builder.ToTable("BackupAttempts", table =>
        {
            table.HasCheckConstraint("CK_BackupAttempts_AttemptNumber", "[AttemptNumber] > 0");
            table.HasCheckConstraint(
                "CK_BackupAttempts_InvocationStatus",
                "[BackupInvocationStatus] IN "
                + "('Prepared', 'Running', 'Succeeded', 'ConfirmedFailed', 'Indeterminate')");
            table.HasCheckConstraint(
                "CK_BackupAttempts_InvocationTimes",
                "([BackupInvocationStatus] = 'Prepared' AND [BackupStartedAtUtc] IS NULL "
                + "AND [BackupFinishedAtUtc] IS NULL) OR "
                + "([BackupInvocationStatus] = 'Running' AND [BackupStartedAtUtc] IS NOT NULL "
                + "AND [BackupFinishedAtUtc] IS NULL) OR "
                + "([BackupInvocationStatus] IN ('Succeeded', 'ConfirmedFailed') "
                + "AND [BackupStartedAtUtc] IS NOT NULL "
                + "AND [BackupFinishedAtUtc] >= [BackupStartedAtUtc]) OR "
                + "([BackupInvocationStatus] = 'Indeterminate' AND [BackupStartedAtUtc] IS NOT NULL "
                + "AND ([BackupFinishedAtUtc] IS NULL OR [BackupFinishedAtUtc] >= [BackupStartedAtUtc]))");
            table.HasCheckConstraint(
                "CK_BackupAttempts_OutcomeCode",
                "([BackupInvocationStatus] IN ('ConfirmedFailed', 'Indeterminate') "
                + "AND LEN([OutcomeCode]) > 0) OR "
                + "([BackupInvocationStatus] NOT IN ('ConfirmedFailed', 'Indeterminate') "
                + "AND [OutcomeCode] IS NULL)");
            table.HasCheckConstraint(
                "CK_BackupAttempts_RequiredPaths",
                "LEN([LocalSqlFilePath]) > 0 AND LEN([WorkerSourceFilePath]) > 0");
            table.HasCheckConstraint(
                "CK_BackupAttempts_RemotePaths",
                "([RemoteStorageTargetId] IS NULL AND [RemotePartialFilePath] IS NULL "
                + "AND [RemoteFinalFilePath] IS NULL) OR "
                + "([RemoteStorageTargetId] IS NOT NULL AND LEN([RemotePartialFilePath]) > 0 "
                + "AND LEN([RemoteFinalFilePath]) > 0 "
                + "AND RIGHT([RemotePartialFilePath], 5) = '.part' "
                + "AND [RemotePartialFilePath] <> [RemoteFinalFilePath])");
            table.HasCheckConstraint(
                "CK_BackupAttempts_LocalVerification",
                "([SourceLengthBytes] IS NULL AND [LocalVerifiedAtUtc] IS NULL) OR "
                + "([SourceLengthBytes] > 0 AND [LocalVerifiedAtUtc] IS NOT NULL "
                + "AND [BackupInvocationStatus] = 'Succeeded')");
            table.HasCheckConstraint(
                "CK_BackupAttempts_EvidenceOrder",
                "([LocalVerifiedAtUtc] IS NULL OR [LocalVerifiedAtUtc] >= [BackupFinishedAtUtc]) "
                + "AND ([RemoteValidatedAtUtc] IS NULL OR ([SourceLengthBytes] > 0 "
                + "AND [RemoteStorageTargetId] IS NOT NULL "
                + "AND [RemoteValidatedAtUtc] >= [LocalVerifiedAtUtc])) "
                + "AND ([LocalCleanupCompletedAtUtc] IS NULL OR ([RemoteValidatedAtUtc] IS NOT NULL "
                + "AND [LocalCleanupCompletedAtUtc] >= [RemoteValidatedAtUtc]))");
        });
        builder.ConfigureConcurrency();
        builder.HasAlternateKey(x => new { x.TaskId, x.Id })
            .HasName("AK_BackupAttempts_TaskId_Id");
        builder.Property(x => x.BackupInvocationStatus)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();
        builder.Property(x => x.PreparedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.BackupStartedAtUtc).HasPrecision(7);
        builder.Property(x => x.BackupFinishedAtUtc).HasPrecision(7);
        builder.Property(x => x.LocalSqlFilePath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.WorkerSourceFilePath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.RemotePartialFilePath).HasMaxLength(2048);
        builder.Property(x => x.RemoteFinalFilePath).HasMaxLength(2048);
        builder.Property(x => x.LocalVerifiedAtUtc).HasPrecision(7);
        builder.Property(x => x.RemoteValidatedAtUtc).HasPrecision(7);
        builder.Property(x => x.LocalCleanupCompletedAtUtc).HasPrecision(7);
        builder.Property(x => x.OutcomeCode).HasMaxLength(100);
        builder.Property<byte[]>("LocalSqlFilePathHash")
            .HasColumnType("binary(32)")
            .HasComputedColumnSql(
                "CONVERT(binary(32), HASHBYTES('SHA2_256', [LocalSqlFilePath]))",
                stored: true);
        builder.Property<byte[]>("RemotePartialFilePathHash")
            .HasColumnType("binary(32)")
            .HasComputedColumnSql(
                "CONVERT(binary(32), HASHBYTES('SHA2_256', [RemotePartialFilePath]))",
                stored: true);
        builder.Property<byte[]>("RemoteFinalFilePathHash")
            .HasColumnType("binary(32)")
            .HasComputedColumnSql(
                "CONVERT(binary(32), HASHBYTES('SHA2_256', [RemoteFinalFilePath]))",
                stored: true);
        builder.HasIndex(x => new { x.TaskId, x.AttemptNumber })
            .IsUnique()
            .HasDatabaseName("UX_BackupAttempts_TaskId_AttemptNumber");
        builder.HasIndex("LocalSqlFilePathHash")
            .IsUnique()
            .HasFilter(null)
            .HasDatabaseName("UX_BackupAttempts_LocalSqlFilePathHash");
        builder.HasIndex(nameof(BackupAttempt.RemoteStorageTargetId), "RemotePartialFilePathHash")
            .IsUnique()
            .HasFilter("[RemoteStorageTargetId] IS NOT NULL AND [RemotePartialFilePath] IS NOT NULL")
            .HasDatabaseName("UX_BackupAttempts_RemotePartialPath");
        builder.HasIndex(nameof(BackupAttempt.RemoteStorageTargetId), "RemoteFinalFilePathHash")
            .IsUnique()
            .HasFilter("[RemoteStorageTargetId] IS NOT NULL AND [RemoteFinalFilePath] IS NOT NULL")
            .HasDatabaseName("UX_BackupAttempts_RemoteFinalPath");
        builder.HasOne<BackupTask>()
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StorageTarget>()
            .WithMany()
            .HasForeignKey(x => x.RemoteStorageTargetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupTaskStateChangeConfiguration
    : IEntityTypeConfiguration<BackupTaskStateChange>
{
    public void Configure(EntityTypeBuilder<BackupTaskStateChange> builder)
    {
        builder.ToTable("BackupTaskStateChanges", table =>
        {
            table.HasCheckConstraint(
                "CK_BackupTaskStateChanges_FromState",
                "([FromStatus] IS NULL AND [FromStage] IS NULL) OR "
                + "([FromStatus] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled') "
                + "AND ([FromStatus] = 'Cancelled' OR [FromStage] IS NOT NULL))");
            table.HasCheckConstraint(
                "CK_BackupTaskStateChanges_ToState",
                "[ToStatus] IN ('Pending', 'Running', 'NeedsAttention', 'Succeeded', 'Failed', 'Cancelled') "
                + "AND ([ToStatus] = 'Cancelled' OR [ToStage] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_BackupTaskStateChanges_Stages",
                "([FromStage] IS NULL OR [FromStage] IN "
                + "('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')) AND "
                + "([ToStage] IS NULL OR [ToStage] IN "
                + "('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup'))");
            table.HasCheckConstraint(
                "CK_BackupTaskStateChanges_ReasonCode_NotEmpty",
                "LEN([ReasonCode]) > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityColumn();
        builder.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.FromStage).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.ToStage).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ReasonCode).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(500);
        builder.Property(x => x.OccurredAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(x => x.MutationId)
            .IsUnique()
            .HasDatabaseName("UX_BackupTaskStateChanges_MutationId");
        builder.HasIndex(x => new { x.TaskId, x.OccurredAtUtc, x.Id })
            .HasDatabaseName("IX_BackupTaskStateChanges_TaskId_OccurredAtUtc");
        builder.HasOne<BackupTask>()
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupAttempt>()
            .WithMany()
            .HasForeignKey(x => new { x.TaskId, AttemptId = x.BackupAttemptId })
            .HasPrincipalKey(x => new { x.TaskId, AttemptId = x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
