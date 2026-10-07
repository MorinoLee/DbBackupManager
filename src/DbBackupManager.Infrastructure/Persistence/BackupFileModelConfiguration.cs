using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupFileConfiguration : IEntityTypeConfiguration<BackupFile>
{
    public void Configure(EntityTypeBuilder<BackupFile> builder)
    {
        builder.ToTable("BackupFiles", table =>
        {
            table.HasCheckConstraint(
                "CK_BackupFiles_Location",
                "[Location] IN ('Local', 'Remote')");
            table.HasCheckConstraint(
                "CK_BackupFiles_Status",
                "[Status] IN ('Available', 'DeletePending', 'DeleteFailed', 'Deleted', 'Missing')");
            table.HasCheckConstraint(
                "CK_BackupFiles_Protocol",
                "[Protocol] IN ('Smb', 'Sftp')");
            table.HasCheckConstraint(
                "CK_BackupFiles_Identity",
                "([Location] = 'Local' AND [DatabaseServerId] IS NOT NULL AND [StorageTargetId] IS NULL) OR "
                + "([Location] = 'Remote' AND [DatabaseServerId] IS NULL AND [StorageTargetId] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_BackupFiles_Path_NotEmpty",
                "LEN([Path]) > 0");
            table.HasCheckConstraint(
                "CK_BackupFiles_Length",
                "[LengthBytes] > 0");
            table.HasCheckConstraint(
                "CK_BackupFiles_Retention",
                "[RetentionDays] BETWEEN 1 AND 36500");
            table.HasCheckConstraint(
                "CK_BackupFiles_DeletionAttemptCount",
                "([Status] = 'Available' AND [DeletionAttemptCount] = 0) OR "
                + "([Status] <> 'Available' AND [DeletionAttemptCount] > 0)");
            table.HasCheckConstraint(
                "CK_BackupFiles_DeletionLease",
                "([Status] = 'DeletePending' AND [DeletionLeaseToken] IS NOT NULL "
                + "AND LEN([DeletionLeaseOwner]) > 0 AND [DeletionLeaseAcquiredAtUtc] IS NOT NULL "
                + "AND [DeletionLeaseAcquiredAtUtc] >= [ValidatedAtUtc] "
                + "AND [DeletionLeaseExpiresAtUtc] > [DeletionLeaseAcquiredAtUtc]) OR "
                + "([Status] <> 'DeletePending' AND [DeletionLeaseToken] IS NULL "
                + "AND [DeletionLeaseOwner] IS NULL AND [DeletionLeaseAcquiredAtUtc] IS NULL "
                + "AND [DeletionLeaseExpiresAtUtc] IS NULL)");
            table.HasCheckConstraint(
                "CK_BackupFiles_DeletionOutcome",
                "([Status] IN ('Available', 'DeletePending') AND [NextDeletionAttemptAtUtc] IS NULL "
                + "AND [DeletionErrorCode] IS NULL AND [DeletedAtUtc] IS NULL AND [MissingDetectedAtUtc] IS NULL) OR "
                + "([Status] = 'DeleteFailed' AND [NextDeletionAttemptAtUtc] IS NOT NULL "
                + "AND LEN([DeletionErrorCode]) > 0 AND [DeletedAtUtc] IS NULL AND [MissingDetectedAtUtc] IS NULL) OR "
                + "([Status] = 'Deleted' AND [NextDeletionAttemptAtUtc] IS NULL "
                + "AND [DeletionErrorCode] IS NULL AND [DeletedAtUtc] >= [ValidatedAtUtc] "
                + "AND [MissingDetectedAtUtc] IS NULL) OR "
                + "([Status] = 'Missing' AND [NextDeletionAttemptAtUtc] IS NULL "
                + "AND [DeletionErrorCode] IS NULL AND [DeletedAtUtc] IS NULL "
                + "AND [MissingDetectedAtUtc] >= [ValidatedAtUtc])");
        });
        builder.ConfigureConcurrency();
        builder.HasOne<BackupSet>().WithMany()
            .HasForeignKey(x => new { x.TaskId, x.AttemptId, x.DatabaseId, x.BackupSetId })
            .HasPrincipalKey(x => new { x.TaskId, x.AttemptId, x.DatabaseId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Location).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.Protocol).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.Path).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ValidatedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.DeletionLeaseOwner).HasMaxLength(200);
        builder.Property(x => x.DeletionLeaseAcquiredAtUtc).HasPrecision(7);
        builder.Property(x => x.DeletionLeaseExpiresAtUtc).HasPrecision(7);
        builder.Property(x => x.NextDeletionAttemptAtUtc).HasPrecision(7);
        builder.Property(x => x.DeletionErrorCode).HasMaxLength(100);
        builder.Property(x => x.DeletedAtUtc).HasPrecision(7);
        builder.Property(x => x.MissingDetectedAtUtc).HasPrecision(7);
        builder.Property<byte[]>("PathHash")
            .HasColumnType("binary(32)")
            .HasComputedColumnSql(
                "CONVERT(binary(32), HASHBYTES('SHA2_256', [Path]))",
                stored: true);
        builder.HasIndex(x => new { x.TaskId, x.Location })
            .IsUnique()
            .HasDatabaseName("UX_BackupFiles_TaskId_Location");
        builder.HasIndex(x => new { x.AttemptId, x.Location })
            .IsUnique()
            .HasDatabaseName("UX_BackupFiles_AttemptId_Location");
        builder.HasIndex(x => x.DeletionLeaseToken)
            .IsUnique()
            .HasFilter("[DeletionLeaseToken] IS NOT NULL")
            .HasDatabaseName("UX_BackupFiles_DeletionLeaseToken");
        builder.HasIndex(nameof(BackupFile.DatabaseServerId), nameof(BackupFile.Protocol), "PathHash")
            .IsUnique()
            .HasFilter("[Location] = 'Local'")
            .HasDatabaseName("UX_BackupFiles_LocalPath");
        builder.HasIndex(nameof(BackupFile.StorageTargetId), nameof(BackupFile.Protocol), "PathHash")
            .IsUnique()
            .HasFilter("[Location] = 'Remote'")
            .HasDatabaseName("UX_BackupFiles_RemotePath");
        builder.HasIndex(x => new
        {
            x.Status,
            x.NextDeletionAttemptAtUtc,
            x.DeletionLeaseExpiresAtUtc,
            x.ValidatedAtUtc,
            x.Id,
        }).HasDatabaseName("IX_BackupFiles_RetentionQueue");
        builder.HasOne<BackupTask>()
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasAlternateKey(x => new { x.TaskId, x.Id })
            .HasName("AK_BackupFiles_TaskId_Id");
        builder.HasOne<BackupAttempt>()
            .WithMany()
            .HasForeignKey(x => new { x.TaskId, x.AttemptId })
            .HasPrincipalKey(x => new { x.TaskId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ManagedDatabase>()
            .WithMany()
            .HasForeignKey(x => x.DatabaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DatabaseServer>()
            .WithMany()
            .HasForeignKey(x => x.DatabaseServerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StorageTarget>()
            .WithMany()
            .HasForeignKey(x => x.StorageTargetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupFileStateChangeConfiguration
    : IEntityTypeConfiguration<BackupFileStateChange>
{
    public void Configure(EntityTypeBuilder<BackupFileStateChange> builder)
    {
        builder.ToTable("BackupFileStateChanges", table =>
        {
            table.HasCheckConstraint(
                "CK_BackupFileStateChanges_FromStatus",
                "[FromStatus] IS NULL OR [FromStatus] IN "
                + "('Available', 'DeletePending', 'DeleteFailed', 'Deleted', 'Missing')");
            table.HasCheckConstraint(
                "CK_BackupFileStateChanges_ToStatus",
                "[ToStatus] IN ('Available', 'DeletePending', 'DeleteFailed', 'Deleted', 'Missing')");
            table.HasCheckConstraint(
                "CK_BackupFileStateChanges_InitialStatus",
                "[FromStatus] IS NOT NULL OR [ToStatus] = 'Available'");
            table.HasCheckConstraint(
                "CK_BackupFileStateChanges_ReasonCode_NotEmpty",
                "LEN([ReasonCode]) > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityColumn();
        builder.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.ReasonCode).HasMaxLength(100).IsRequired();
        builder.Property(x => x.OccurredAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(x => x.MutationId)
            .IsUnique()
            .HasDatabaseName("UX_BackupFileStateChanges_MutationId");
        builder.HasIndex(x => new { x.FileId, x.OccurredAtUtc, x.Id })
            .HasDatabaseName("IX_BackupFileStateChanges_FileId_OccurredAtUtc");
        builder.HasIndex(x => new { x.TaskId, x.OccurredAtUtc, x.Id })
            .HasDatabaseName("IX_BackupFileStateChanges_TaskId_OccurredAtUtc");
        builder.HasOne<BackupFile>()
            .WithMany()
            .HasForeignKey(x => new { x.TaskId, x.FileId })
            .HasPrincipalKey(x => new { x.TaskId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupTask>()
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TaskEventConfiguration : IEntityTypeConfiguration<TaskEvent>
{
    public void Configure(EntityTypeBuilder<TaskEvent> builder)
    {
        builder.ToTable("TaskEvents");
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.OccurredAtUtc).HasPrecision(7);
        builder.Property(x => x.PublishedAtUtc).HasPrecision(7);
        builder.HasOne<BackupTask>()
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
        // 未发布读取（Relay）与已发布清理（P6.3）使用同一键、相反过滤条件，是两个不同的筛选索引。
        // 必须使用带名称的 HasIndex 重载：不带名称的重载会按属性查找并复用已有索引，导致两次配置
        // 落到同一个索引上，生成的迁移变成「删掉 Relay 索引再建清理索引」，使未发布读取退化为扫描。
        builder.HasIndex(x => new { x.OccurredAtUtc, x.EventId }, "IX_TaskEvents_OccurredAtUtc_EventId")
            .HasFilter("[PublishedAtUtc] IS NULL");
        builder.HasIndex(x => new { x.OccurredAtUtc, x.EventId }, "IX_TaskEvents_Published_OccurredAtUtc_EventId")
            .HasFilter("[PublishedAtUtc] IS NOT NULL");
    }
}
