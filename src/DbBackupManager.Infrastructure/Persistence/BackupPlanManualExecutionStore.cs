using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupPlanManualExecutionStore(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock)
    : IBackupPlanManualExecutionStore
{
    private readonly BackupTaskPersistence _persistence = new(factory);

    public Task<BackupPlanTaskCreationResult> CreateAsync(
        CreateBackupPlanTaskCommand command, CancellationToken cancellationToken = default)
    {
        var request = command.Request;
        if (request.Purpose == BackupRunPurpose.PlanLog)
        {
            return Task.FromResult(new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.StageNotOpen));
        }

        if (request.Purpose is not (BackupRunPurpose.PlanFull or BackupRunPurpose.PlanDifferential or BackupRunPurpose.AdHocCopyOnlyFull))
        {
            return Task.FromResult(new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.InvalidRequest));
        }

        return _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var plan = await db.BackupPlans.FromSqlInterpolated(
                    $"SELECT * FROM [BackupPlans] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.PlanId}")
                .Include(item => item.Versions)
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
            if (plan is null)
            {
                return new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.NotFound);
            }

            var current = new CreateBackupPlanTaskCommand(
                request with { PlanVersionId = plan.CurrentVersionId },
                BackupTaskTriggerType.Manual,
                clock.GetUtcNow().ToUniversalTime(),
                command.ActorAdminUserId,
                command.ActorSecurityStamp);
            var replay = await BackupPlanTaskCreationCore.FindReplayAsync(db, current, cancellationToken);
            if (replay is not null)
            {
                if (replay.Code is BackupPlanTaskCreationCode.AlreadyApplied or BackupPlanTaskCreationCode.AlreadyExists)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return replay;
            }

            if (await ReplayByTaskIdAsync(db, current.Request, cancellationToken) is { } taskReplay)
            {
                if (taskReplay.Code is BackupPlanTaskCreationCode.AlreadyApplied or BackupPlanTaskCreationCode.AlreadyExists)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return taskReplay;
            }

            if (request.Purpose == BackupRunPurpose.PlanDifferential)
            {
                var facts = await BackupPlanBaselineFacts.ReadAsync(db, plan.DatabaseId, cancellationToken);
                if (!BackupPlanBaselineRules.AllowsDifferentialExecution(
                        facts.HasVerifiedManagedOrdinaryFull, facts.ActiveBaselineConclusion))
                {
                    return new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.BaselineRequired);
                }
            }

            var result = await BackupPlanTaskCreationCore.CreateAsync(db, plan, current, resolveOnly: false, cancellationToken);
            if (result.Code is BackupPlanTaskCreationCode.Created or BackupPlanTaskCreationCode.AlreadyApplied or BackupPlanTaskCreationCode.AlreadyExists)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }, cancellationToken);
    }

    private static async Task<BackupPlanTaskCreationResult?> ReplayByTaskIdAsync(
        PlatformDbContext db, CreateBackupPlanTaskRequest request, CancellationToken cancellationToken)
    {
        if (request.TaskId == Guid.Empty
            || !await db.BackupTasks.AsNoTracking().AnyAsync(item => item.Id == request.TaskId, cancellationToken))
        {
            return null;
        }

        var snapshot = await db.BackupTaskSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TaskId == request.TaskId, cancellationToken);
        var task = await db.BackupTasks.AsNoTracking().SingleAsync(item => item.Id == request.TaskId, cancellationToken);
        if (snapshot?.Purpose != request.Purpose || task.PlanId != request.PlanId)
        {
            return new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.RequestConflict);
        }

        return new BackupPlanTaskCreationResult(
            BackupPlanTaskCreationCode.AlreadyApplied,
            new CreatedBackupPlanTask(
                task.Id,
                task.PlanId!.Value,
                task.PlanVersionId!.Value,
                snapshot.DatabaseId,
                snapshot.Purpose!.Value,
                task.BackupType,
                task.TriggerType,
                task.ScheduledSlotAtUtc,
                task.CoveredDifferentialSlotUtc));
    }
}
