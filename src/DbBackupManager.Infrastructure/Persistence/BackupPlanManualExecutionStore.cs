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

            if (request.Purpose == BackupRunPurpose.PlanDifferential)
            {
                var facts = await BackupPlanBaselineFacts.ReadAsync(db, plan.DatabaseId, cancellationToken);
                if (!BackupPlanBaselineRules.AllowsDifferentialExecution(
                        facts.HasVerifiedManagedOrdinaryFull, facts.ActiveBaselineConclusion))
                {
                    return new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.BaselineRequired);
                }
            }

            var current = new CreateBackupPlanTaskCommand(
                request with { PlanVersionId = plan.CurrentVersionId },
                BackupTaskTriggerType.Manual,
                clock.GetUtcNow().ToUniversalTime(),
                command.ActorAdminUserId,
                command.ActorSecurityStamp);
            var result = await BackupPlanTaskCreationCore.CreateAsync(db, plan, current, resolveOnly: false, cancellationToken);
            if (result.Code is BackupPlanTaskCreationCode.Created or BackupPlanTaskCreationCode.AlreadyApplied or BackupPlanTaskCreationCode.AlreadyExists)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }, cancellationToken);
    }
}
