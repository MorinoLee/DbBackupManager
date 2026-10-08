using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Domain.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DbBackupManager.Infrastructure.Persistence;

public sealed partial class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<BackupFile> BackupFiles => Set<BackupFile>();

    public DbSet<BackupSet> BackupSets => Set<BackupSet>();

    public DbSet<BackupSetEvidence> BackupSetEvidence => Set<BackupSetEvidence>();

    public DbSet<BackupFileStateChange> BackupFileStateChanges => Set<BackupFileStateChange>();

    public DbSet<TaskEvent> TaskEvents => Set<TaskEvent>();

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    public DbSet<CredentialReference> CredentialReferences => Set<CredentialReference>();

    public DbSet<DatabaseServer> DatabaseServers => Set<DatabaseServer>();

    public DbSet<DatabaseInstance> DatabaseInstances => Set<DatabaseInstance>();

    public DbSet<ManagedDatabase> ManagedDatabases => Set<ManagedDatabase>();

    public DbSet<StorageTarget> StorageTargets => Set<StorageTarget>();

    public DbSet<BackupPolicy> BackupPolicies => Set<BackupPolicy>();

    public DbSet<BackupPlan> BackupPlans => Set<BackupPlan>();

    public DbSet<BackupPlanVersion> BackupPlanVersions => Set<BackupPlanVersion>();

    public DbSet<BackupTask> BackupTasks => Set<BackupTask>();

    public DbSet<BackupTaskSnapshot> BackupTaskSnapshots => Set<BackupTaskSnapshot>();

    public DbSet<BackupAttempt> BackupAttempts => Set<BackupAttempt>();

    public DbSet<BackupTaskStateChange> BackupTaskStateChanges => Set<BackupTaskStateChange>();

    public DbSet<SmtpSettings> SmtpSettings => Set<SmtpSettings>();

    public DbSet<SmtpRecipient> SmtpRecipients => Set<SmtpRecipient>();

    public DbSet<NotificationOutbox> NotificationOutbox => Set<NotificationOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareEntries();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateException exception) when (IsPlanVersionNumberConflict(exception))
        {
            foreach (var entry in ChangeTracker.Entries<BackupPlan>().Where(item => item.State == EntityState.Modified))
            {
                if (PlanHasChanged(entry, entry.GetDatabaseValues()))
                {
                    throw new DbUpdateConcurrencyException("备份计划已被其他上下文修改，请重新读取后再修改。", exception);
                }
            }

            throw;
        }
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareEntries();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsPlanVersionNumberConflict(exception))
        {
            foreach (var entry in ChangeTracker.Entries<BackupPlan>().Where(item => item.State == EntityState.Modified))
            {
                if (PlanHasChanged(entry, await entry.GetDatabaseValuesAsync(cancellationToken)))
                {
                    throw new DbUpdateConcurrencyException("备份计划已被其他上下文修改，请重新读取后再修改。", exception);
                }
            }

            throw;
        }
    }

    // 循环外键要求先插入新版本，版本号冲突可能早于计划指针的 rowversion 检查发生。
    private static bool IsPlanVersionNumberConflict(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 } sqlException
        && sqlException.Message.Contains("UX_BackupPlanVersions_PlanId_Number", StringComparison.Ordinal);

    private static bool PlanHasChanged(EntityEntry<BackupPlan> entry, PropertyValues? stored) =>
        stored is null || !entry.OriginalValues.GetValue<byte[]>(nameof(BackupPlan.RowVersion))
            .SequenceEqual(stored.GetValue<byte[]>(nameof(BackupPlan.RowVersion)));

    private void PrepareEntries()
    {
        PrepareTaskIdentityEntries();
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<ConcurrentEntity>())
        {
            if (entry.Entity is AdminUser or BackupTask or BackupAttempt or BackupFile or BackupSet
                    or Domain.Notifications.NotificationOutbox or Domain.Notifications.SmtpSettings
                && entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException("受保护的业务记录不能通过普通 EF 写路径直接删除。");
            }

            if (entry.State == EntityState.Added)
            {
                entry.Property(x => x.CreatedAtUtc).CurrentValue = now;
            }

            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Property(x => x.UpdatedAtUtc).CurrentValue = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<BackupPlanVersion>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("备份计划版本只能追加，不能修改或删除。");
            }
        }

        foreach (var entry in ChangeTracker.Entries<BackupSetEvidence>())
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("备份集证据只能追加，不能修改或删除。");

        foreach (var entry in ChangeTracker.Entries<BackupSet>().Where(x => x.State == EntityState.Modified))
            ValidateBackupSetChange(entry);

        foreach (var entry in ChangeTracker.Entries<AuditRecord>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("审计记录只能追加，不能修改或删除。");
            }

            if (entry.State == EntityState.Added)
            {
                entry.Property(x => x.OccurredAtUtc).CurrentValue = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<BackupTaskSnapshot>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("任务快照创建后不能修改或删除。");
            }
        }

        foreach (var entry in ChangeTracker.Entries<TaskEvent>())
        {
            if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified
                && entry.Properties.Any(p => p.IsModified && p.Metadata.Name != nameof(TaskEvent.PublishedAtUtc)))
                throw new InvalidOperationException("任务事件仅允许推进投递标记。");
        }
        // 与状态历史在同一个 SaveChanges 事务生成事件，MutationId 也是幂等事件标识。
        var taskChanges = ChangeTracker.Entries<BackupTaskStateChange>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => (entry.Entity.MutationId, entry.Entity.TaskId, entry.Entity.OccurredAtUtc));
        var fileChanges = ChangeTracker.Entries<BackupFileStateChange>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => (entry.Entity.MutationId, entry.Entity.TaskId, entry.Entity.OccurredAtUtc));
        foreach (var change in taskChanges.Concat(fileChanges).DistinctBy(change => change.MutationId).ToArray())
            if (!TaskEvents.Local.Any(x => x.EventId == change.MutationId))
                TaskEvents.Add(new(change.MutationId, change.TaskId, change.OccurredAtUtc));

        foreach (var entry in ChangeTracker.Entries<BackupTaskStateChange>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("任务状态历史只能追加，不能修改或删除。");
            }
        }

        foreach (var entry in ChangeTracker.Entries<BackupFileStateChange>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("备份文件状态历史只能追加，不能修改或删除。");
            }
        }

        foreach (var entry in ChangeTracker.Entries<BackupFile>())
        {
            if (entry.State == EntityState.Modified)
            {
                ValidateBackupFileChange(entry);
            }
        }

        foreach (var entry in ChangeTracker.Entries<BackupAttempt>())
        {
            if (entry.State == EntityState.Modified)
            {
                ValidateBackupAttemptChange(entry);
            }
        }
    }

    private static void ValidateBackupAttemptChange(EntityEntry<BackupAttempt> entry)
    {
        string[] immutableProperties =
        [
            nameof(BackupAttempt.TaskId),
            nameof(BackupAttempt.AttemptNumber),
            nameof(BackupAttempt.PreparedAtUtc),
            nameof(BackupAttempt.LocalSqlFilePath),
            nameof(BackupAttempt.WorkerSourceFilePath),
            nameof(BackupAttempt.RemoteStorageTargetId),
            nameof(BackupAttempt.RemotePartialFilePath),
            nameof(BackupAttempt.RemoteFinalFilePath),
        ];
        string[] writeOnceProperties =
        [
            nameof(BackupAttempt.BackupStartedAtUtc),
            nameof(BackupAttempt.BackupFinishedAtUtc),
            nameof(BackupAttempt.SourceLengthBytes),
            nameof(BackupAttempt.LocalVerifiedAtUtc),
            nameof(BackupAttempt.RemoteValidatedAtUtc),
            nameof(BackupAttempt.LocalCleanupCompletedAtUtc),
        ];

        foreach (var propertyName in immutableProperties)
        {
            RejectChangedProperty(entry, propertyName, "Attempt 身份和路径创建后不能修改。");
        }

        foreach (var propertyName in writeOnceProperties)
        {
            var property = entry.Property(propertyName);
            if (property.IsModified
                && property.OriginalValue is not null
                && !Equals(property.OriginalValue, property.CurrentValue))
            {
                throw new InvalidOperationException("Attempt 已写入的执行证据不能覆盖。");
            }
        }

        var status = entry.Property(x => x.BackupInvocationStatus);
        var outcomeCode = entry.Property(x => x.OutcomeCode);
        if (outcomeCode.IsModified
            && !Equals(outcomeCode.OriginalValue, outcomeCode.CurrentValue)
            && (!status.IsModified || status.OriginalValue == status.CurrentValue))
        {
            throw new InvalidOperationException("Attempt 结果代码只能随调用状态的单向转换写入。");
        }

        if (!status.IsModified || status.OriginalValue == status.CurrentValue)
        {
            return;
        }

        var isAllowed = (status.OriginalValue, status.CurrentValue) switch
        {
            (BackupInvocationStatus.Prepared, BackupInvocationStatus.Running) => true,
            (BackupInvocationStatus.Running, BackupInvocationStatus.Succeeded) => true,
            (BackupInvocationStatus.Running, BackupInvocationStatus.ConfirmedFailed) => true,
            (BackupInvocationStatus.Running, BackupInvocationStatus.Indeterminate) => true,
            (BackupInvocationStatus.Indeterminate, BackupInvocationStatus.Succeeded) => true,
            (BackupInvocationStatus.Indeterminate, BackupInvocationStatus.ConfirmedFailed) => true,
            _ => false,
        };
        if (!isAllowed)
        {
            throw new InvalidOperationException("Attempt 调用状态只能按既定方向前进。");
        }
    }

    private static void ValidateBackupFileChange(EntityEntry<BackupFile> entry)
    {
        string[] immutableProperties =
        [
            nameof(BackupFile.TaskId),
            nameof(BackupFile.AttemptId),
            nameof(BackupFile.DatabaseId),
            nameof(BackupFile.BackupSetId),
            nameof(BackupFile.Location),
            nameof(BackupFile.DatabaseServerId),
            nameof(BackupFile.StorageTargetId),
            nameof(BackupFile.Protocol),
            nameof(BackupFile.Path),
            nameof(BackupFile.LengthBytes),
            nameof(BackupFile.ValidatedAtUtc),
            nameof(BackupFile.RetentionDays),
        ];

        foreach (var propertyName in immutableProperties)
        {
            var property = entry.Property(propertyName);
            if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
            {
                throw new InvalidOperationException("备份文件身份和验证事实创建后不能修改。");
            }
        }

        var status = entry.Property(file => file.Status);
        if (!status.IsModified || status.OriginalValue == status.CurrentValue)
        {
            return;
        }

        var isAllowed = (status.OriginalValue, status.CurrentValue) switch
        {
            (BackupFileStatus.Available, BackupFileStatus.DeletePending) => true,
            (BackupFileStatus.Available, BackupFileStatus.Missing) => true,
            (BackupFileStatus.DeleteFailed, BackupFileStatus.DeletePending) => true,
            (BackupFileStatus.DeletePending, BackupFileStatus.DeleteFailed) => true,
            (BackupFileStatus.DeletePending, BackupFileStatus.Deleted) => true,
            (BackupFileStatus.DeletePending, BackupFileStatus.Missing) => true,
            _ => false,
        };
        if (!isAllowed)
        {
            throw new InvalidOperationException("备份文件状态只能按删除协议单向转换。");
        }
    }

    private static void RejectChangedProperty(
        EntityEntry<BackupAttempt> entry,
        string propertyName,
        string message)
    {
        var property = entry.Property(propertyName);
        if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void ValidateBackupSetChange(EntityEntry<BackupSet> entry)
    {
        foreach (var name in new[] { nameof(BackupSet.TaskId), nameof(BackupSet.AttemptId), nameof(BackupSet.DatabaseId) })
        {
            var property = entry.Property(name);
            if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
                throw new InvalidOperationException("备份集来源身份不能修改。");
        }
        foreach (var field in entry.ComplexProperty(x => x.Metadata).ComplexProperties)
        {
            var state = field.Property("State");
            var value = field.Property("Value");
            if ((BackupMetadataState)state.OriginalValue! != BackupMetadataState.Unknown
                && (!Equals(state.OriginalValue, state.CurrentValue) || !Equals(value.OriginalValue, value.CurrentValue)))
                throw new InvalidOperationException("已确认的备份集事实不能覆盖。");
        }
        foreach (var name in new[] { nameof(BackupSet.SqlSuccessObserved), nameof(BackupSet.HasMetadataConflict) })
            if (entry.Property(name).OriginalValue is true && entry.Property(name).CurrentValue is false)
                throw new InvalidOperationException("已观察到的 SQL 成功或事实冲突不能静默清除。");
        var dependency = entry.Property(x => x.BaseBackupSetId);
        if (dependency.OriginalValue is not null && dependency.OriginalValue != dependency.CurrentValue)
            throw new InvalidOperationException("已确认的备份集依赖不能覆盖。");
        var count = entry.Property(x => x.ReconciliationCount);
        if (count.CurrentValue < count.OriginalValue)
            throw new InvalidOperationException("备份集核对次数不能倒退。");
        var completion = entry.ComplexProperty(x => x.Completion);
        var source = completion.Property(x => x.Source);
        var time = completion.Property(x => x.CompletedAtUtc);
        var reason = completion.Property(x => x.ReasonCode);
        if (source.OriginalValue != BackupCompletionTimeSource.Unknown
            && !(source.OriginalValue == BackupCompletionTimeSource.SqlLocalTime
                && source.CurrentValue == BackupCompletionTimeSource.PlatformObserved)
            && (source.OriginalValue != source.CurrentValue || time.OriginalValue != time.CurrentValue
                || reason.OriginalValue != reason.CurrentValue))
            throw new InvalidOperationException("已确认完成时间只能从 SQL 推定提升为平台观察事实。");
        entry.Entity.Metadata.Validate();
        entry.Entity.Assessment.Validate();
        BackupSetCodes.ValidateCompletion(entry.Entity.Completion);
    }
}
