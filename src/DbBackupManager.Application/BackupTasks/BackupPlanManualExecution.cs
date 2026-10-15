using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public interface IBackupPlanManualExecution
{
    Task<BackupPlanTaskCreationResult> RunAsync(
        AdminSession actor,
        Guid planId,
        Guid taskId,
        Guid mutationId,
        BackupRunPurpose purpose,
        CancellationToken cancellationToken = default);
}

public interface IBackupPlanManualExecutionStore
{
    Task<BackupPlanTaskCreationResult> CreateAsync(
        CreateBackupPlanTaskCommand command,
        CancellationToken cancellationToken = default);
}

/// <summary>管理员明确发起的计划内 FULL/DIFF 或临时 COPY_ONLY。不执行目标 SQL。</summary>
public sealed class BackupPlanManualExecutionService(IBackupPlanManualExecutionStore store) : IBackupPlanManualExecution
{
    public Task<BackupPlanTaskCreationResult> RunAsync(
        AdminSession actor,
        Guid planId,
        Guid taskId,
        Guid mutationId,
        BackupRunPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (purpose == BackupRunPurpose.PlanLog)
        {
            return Task.FromResult(new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.StageNotOpen));
        }

        var request = new CreateBackupPlanTaskRequest(taskId, mutationId, planId, planId, purpose);
        return store.CreateAsync(
            new CreateBackupPlanTaskCommand(request, BackupTaskTriggerType.Manual, DateTimeOffset.UtcNow, actor.AdminUserId, actor.SecurityStamp),
            cancellationToken);
    }
}
