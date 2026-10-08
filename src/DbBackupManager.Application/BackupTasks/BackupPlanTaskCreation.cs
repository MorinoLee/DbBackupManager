using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupTasks;

public sealed record CreateBackupPlanTaskRequest(
    Guid TaskId, Guid MutationId, Guid PlanId, Guid PlanVersionId, BackupRunPurpose Purpose,
    DateTimeOffset? ScheduledSlotAtUtc = null, DateTimeOffset? CoveredDifferentialSlotUtc = null);

public sealed record CreateBackupPlanTaskCommand(
    CreateBackupPlanTaskRequest Request, BackupTaskTriggerType TriggerType, DateTimeOffset NowUtc,
    Guid? ActorAdminUserId = null, string? ActorSecurityStamp = null);

public enum BackupPlanTaskCreationCode
{
    Created, AlreadyApplied, AlreadyExists, InvalidRequest, AuthenticationRequired,
    NotFound, VersionConflict, PurposeNotInMode, PlanPaused, InvalidSlot,
    ConfigurationUnavailable, RequestConflict, ConcurrencyConflict, StageNotOpen
}

public sealed record CreatedBackupPlanTask(
    Guid TaskId, Guid PlanId, Guid PlanVersionId, Guid DatabaseId, BackupRunPurpose Purpose,
    BackupType BackupType, BackupTaskTriggerType TriggerType, DateTimeOffset? ScheduledSlotAtUtc,
    DateTimeOffset? CoveredDifferentialSlotUtc);

public sealed record BackupPlanTaskCreationResult(BackupPlanTaskCreationCode Code, CreatedBackupPlanTask? Task = null);

public interface IBackupPlanTaskCreationStore
{
    Task<BackupPlanTaskCreationResult> CreateAsync(CreateBackupPlanTaskCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>只创建平台任务，不解密凭据、不执行备份或访问文件。</summary>
public sealed class BackupPlanTaskCreationService(IBackupPlanTaskCreationStore store, TimeProvider clock)
{
    public Task<BackupPlanTaskCreationResult> CreateManualAsync(
        CreateBackupPlanTaskRequest request, AdminSession actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        return store.CreateAsync(new(request, BackupTaskTriggerType.Manual, clock.GetUtcNow(),
            actor.AdminUserId, actor.SecurityStamp), cancellationToken);
    }

    public Task<BackupPlanTaskCreationResult> CreateScheduledAsync(
        CreateBackupPlanTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return store.CreateAsync(new(request, BackupTaskTriggerType.Scheduled, clock.GetUtcNow()), cancellationToken);
    }
}
