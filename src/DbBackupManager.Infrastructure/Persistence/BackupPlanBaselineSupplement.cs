using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal enum BackupPlanBaselineSupplementCode
{
    None = 1,
    Queued = 2,
    Skipped = 3,
    AlreadyApplied = 4,
    Conflict = 5
}

internal readonly record struct BackupPlanBaselineSupplementResult(
    BackupPlanBaselineSupplementCode Code, Guid? TaskId);

internal static class BackupPlanBaselineSupplementCore
{
    internal static async Task<BackupPlanBaselineSupplementResult> ApplyAsync(
        PlatformDbContext db,
        BackupPlan plan,
        AdminSession actor,
        Guid mutationId,
        DateTimeOffset nowUtc,
        bool resumedFromPause,
        CancellationToken cancellationToken)
    {
        if (mutationId == Guid.Empty)
        {
            throw new ArgumentException("启用事件的 MutationId 不能为空。", nameof(mutationId));
        }

        var versions = plan.Versions.OrderBy(version => version.Number).ToArray();
        var current = versions[^1];
        var previousIncludes = versions.Length > 1
            && BackupPlanRules.Includes(versions[^2].Mode, BackupType.Differential);
        var replay = await db.BackupPlanBaselineEnablements.AsNoTracking()
            .SingleOrDefaultAsync(item => item.MutationId == mutationId, cancellationToken);
        if (replay is not null)
        {
            return replay.PlanId == plan.Id && replay.PlanVersionId == current.Id
                ? new(BackupPlanBaselineSupplementCode.AlreadyApplied, replay.TaskId)
                : new(BackupPlanBaselineSupplementCode.Conflict, null);
        }

        var currentIncludes = BackupPlanRules.Includes(current.Mode, BackupType.Differential);
        var facts = await BackupPlanBaselineFacts.ReadAsync(db, plan.DatabaseId, cancellationToken);
        var decision = BackupPlanBaselineRules.Decide(
            plan.IsPaused,
            currentIncludes,
            previousIncludes,
            facts.HasVerifiedManagedOrdinaryFull,
            facts.ActiveBaselineConclusion,
            facts.HasOutstandingOrdinaryPlanFull,
            resumedFromPause);
        if (decision is null)
        {
            return new(BackupPlanBaselineSupplementCode.None, null);
        }

        if (!resumedFromPause)
        {
            var existing = await db.BackupPlanBaselineEnablements.AsNoTracking()
                .Where(item => item.PlanId == plan.Id && item.PlanVersionId == current.Id)
                .OrderByDescending(item => item.EnablementSequence)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                return new(BackupPlanBaselineSupplementCode.AlreadyApplied, existing.TaskId);
            }
        }

        Guid? taskId = null;
        if (decision == BackupPlanBaselineEnablementDecision.Queued)
        {
            taskId = Guid.NewGuid();
            var created = await BackupPlanTaskCreationCore.CreateAsync(
                db,
                plan,
                new CreateBackupPlanTaskCommand(
                    new CreateBackupPlanTaskRequest(taskId.Value, mutationId, plan.Id, current.Id, BackupRunPurpose.PlanFull),
                    BackupTaskTriggerType.BaselineSupplement,
                    nowUtc,
                    actor.AdminUserId,
                    actor.SecurityStamp),
                resolveOnly: false,
                cancellationToken);
            if (created.Code is not (BackupPlanTaskCreationCode.Created or BackupPlanTaskCreationCode.AlreadyApplied or BackupPlanTaskCreationCode.AlreadyExists)
                || created.Task is null
                || created.Task.Purpose != BackupRunPurpose.PlanFull
                || created.Task.PlanVersionId != current.Id)
            {
                throw new InvalidOperationException("补基线的计划内完整备份未能创建。");
            }

            taskId = created.Task.TaskId;
        }

        var sequence = (await db.BackupPlanBaselineEnablements.AsNoTracking()
            .Where(item => item.PlanId == plan.Id)
            .Select(item => (int?)item.EnablementSequence)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        db.BackupPlanBaselineEnablements.Add(new BackupPlanBaselineEnablement(
            Guid.NewGuid(), plan.Id, current.Id, sequence, mutationId, taskId, decision.Value));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsEnablementUnique(exception))
        {
            return new(BackupPlanBaselineSupplementCode.Conflict, null);
        }

        return new(
            decision == BackupPlanBaselineEnablementDecision.Queued
                ? BackupPlanBaselineSupplementCode.Queued
                : BackupPlanBaselineSupplementCode.Skipped,
            taskId);
    }

    internal static bool IsEnablementUnique(DbUpdateException exception) =>
        exception.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains("BackupPlanBaselineEnablements", StringComparison.Ordinal);
}

internal sealed class BackupPlanBaselineSupplementStore(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock)
{
    private readonly BackupTaskPersistence _persistence = new(factory);

    internal async Task<BackupPlanBaselineSupplementResult> SupplementAsync(
        Guid planId, Guid mutationId, AdminSession actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return await _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var plan = await db.BackupPlans.FromSqlInterpolated(
                    $"SELECT * FROM [BackupPlans] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {planId}")
                .Include(item => item.Versions)
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
            if (plan is null)
            {
                return new BackupPlanBaselineSupplementResult(BackupPlanBaselineSupplementCode.None, null);
            }

            var result = await BackupPlanBaselineSupplementCore.ApplyAsync(
                db, plan, actor, mutationId, clock.GetUtcNow().ToUniversalTime(), resumedFromPause: false, cancellationToken);
            if (result.Code == BackupPlanBaselineSupplementCode.Conflict)
            {
                return result;
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }
}
