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

            // 成功重放也必须在返回前复核管理员状态与安全戳，不能依赖周期性的 Cookie/Circuit 重验证。
            if (command.ActorAdminUserId is null || command.ActorAdminUserId == Guid.Empty
                || !await BackupTaskPersistence.IsActorValidAsync(
                    db, command.ActorAdminUserId, command.ActorSecurityStamp, cancellationToken))
            {
                return new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.AuthenticationRequired);
            }

            var normalized = command with { NowUtc = clock.GetUtcNow().ToUniversalTime() };

            // 先按持久化原回执匹配完整请求，命中即返回原任务，不按当前计划版本重写重放身份。
            if (await BackupPlanTaskCreationCore.FindReplayForManualAsync(db, normalized, cancellationToken) is { } replay)
            {
                if (replay.Code is BackupPlanTaskCreationCode.AlreadyApplied or BackupPlanTaskCreationCode.AlreadyExists)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return replay;
            }

            // 复用已有任务时原子绑定本次 MutationId，避免同一 MutationId 之后指向另一个任务。
            if (await BackupPlanTaskCreationCore.ReplayManualByTaskIdAsync(db, normalized, cancellationToken) is { } taskReplay)
            {
                if (taskReplay.Code is BackupPlanTaskCreationCode.AlreadyApplied or BackupPlanTaskCreationCode.AlreadyExists)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return taskReplay;
            }

            // 只有全新请求才绑定当前计划版本。
            var current = normalized with { Request = request with { PlanVersionId = plan.CurrentVersionId } };
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
}
