using System.Data;
using System.Globalization;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupPlanTaskCreationStore(IDbContextFactory<PlatformDbContext> contextFactory)
    : IBackupPlanTaskCreationStore
{
    private const string CreatedReason = "task.plan.created";
    private const string ReusedReason = "task.plan.creation_reused";
    private readonly BackupTaskPersistence _persistence = new(contextFactory);

    public async Task<BackupPlanTaskCreationResult> CreateAsync(CreateBackupPlanTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);
        var request = command.Request;
        if (request.MutationId == Guid.Empty || command.NowUtc.Offset != TimeSpan.Zero)
            return new(BackupPlanTaskCreationCode.InvalidRequest);
        try
        {
            return await ExecuteAsync(command, resolveOnly: false, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(BackupPlanTaskCreationCode.ConcurrencyConflict);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // 补查也经过身份校验及同一份完整请求核对，不能仅凭 MutationId 返回成功。
            try { return await ExecuteAsync(command, resolveOnly: true, cancellationToken); }
            catch (DbUpdateException) { return new(BackupPlanTaskCreationCode.ConcurrencyConflict); }
        }
        catch (DbUpdateException)
        {
            return new(BackupPlanTaskCreationCode.ConcurrencyConflict);
        }
    }

    private Task<BackupPlanTaskCreationResult> ExecuteAsync(CreateBackupPlanTaskCommand command, bool resolveOnly,
        CancellationToken token) => _persistence.ExecuteWithStrategyAsync<BackupPlanTaskCreationResult>(async context =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var request = command.Request;
        // 同一计划的创建与版本修改在提交点串行；指定旧版本的手动请求不会改用新版本。
        var plan = await context.BackupPlans.FromSqlInterpolated(
                $"SELECT * FROM [BackupPlans] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.PlanId}")
            .Include(x => x.Versions).SingleOrDefaultAsync(token);
        if (command.TriggerType == BackupTaskTriggerType.Manual
                && (command.ActorAdminUserId is null || command.ActorAdminUserId == Guid.Empty)
            || command.TriggerType == BackupTaskTriggerType.Scheduled && command.ActorAdminUserId is not null
            || !await IsActorValidAsync(context, command.ActorAdminUserId, command.ActorSecurityStamp, token))
            return new(BackupPlanTaskCreationCode.AuthenticationRequired);

        var receipt = await context.BackupTaskStateChanges.SingleOrDefaultAsync(x => x.MutationId == request.MutationId, token);
        if (receipt is not null)
        {
            if (receipt.ReasonCode is not (CreatedReason or ReusedReason)
                || !string.Equals(receipt.Message, RequestIdentity(command), StringComparison.Ordinal))
                return new(BackupPlanTaskCreationCode.RequestConflict);
            var original = await ReadIdentityAsync(context, receipt.TaskId, token);
            return original is null ? new(BackupPlanTaskCreationCode.RequestConflict)
                : new(BackupPlanTaskCreationCode.AlreadyApplied, original);
        }
        if (request.TaskId == Guid.Empty || request.PlanId == Guid.Empty || request.PlanVersionId == Guid.Empty
            || !Enum.IsDefined(request.Purpose) || !Enum.IsDefined(command.TriggerType))
            return new(BackupPlanTaskCreationCode.InvalidRequest);
        if (plan is null) return new(BackupPlanTaskCreationCode.NotFound);
        var invalid = BackupPlanTaskCreationRules.Validate(command, plan);
        if (invalid is { } code) return new(code);

        if (command.TriggerType == BackupTaskTriggerType.Scheduled)
        {
            var type = BackupPlanRules.ToBackupType(request.Purpose);
            var existing = await context.BackupTasks.SingleOrDefaultAsync(x => x.PlanId == request.PlanId
                && x.PlanVersionId == request.PlanVersionId && x.BackupType == type
                && x.ScheduledSlotAtUtc == request.ScheduledSlotAtUtc, token);
            if (existing is not null)
            {
                var identity = await ReadIdentityAsync(context, existing.Id, token);
                if (identity is null || identity.Purpose != request.Purpose
                    || identity.CoveredDifferentialSlotUtc != request.CoveredDifferentialSlotUtc)
                    return new(BackupPlanTaskCreationCode.RequestConflict);
                // 竞争落败的 MutationId 也绑定原请求，避免以后换 TaskId/用途/时隙重放。
                AddReceipt(context, command, existing, reused: true);
                await context.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return new(BackupPlanTaskCreationCode.AlreadyExists, identity);
            }
        }
        if (resolveOnly) return new(BackupPlanTaskCreationCode.ConcurrencyConflict);
        if (await context.BackupTasks.AnyAsync(x => x.Id == request.TaskId, token))
            return new(BackupPlanTaskCreationCode.RequestConflict);

        var version = plan.Versions.Single(x => x.Id == request.PlanVersionId);
        BackupTask task;
        BackupTaskSnapshot snapshot;
        try
        {
            var configuration = await BackupTaskConfigurationReader.ReadAsync(context, plan.DatabaseId,
                version.StorageTargetId, BackupTaskPathFactory.PlanVersion, token);
            if (configuration is null) return new(BackupPlanTaskCreationCode.ConfigurationUnavailable);
            task = BackupTask.ForPlan(request.TaskId, plan.Id, version.Id, request.Purpose, command.TriggerType,
                request.ScheduledSlotAtUtc, request.CoveredDifferentialSlotUtc);
            snapshot = BackupTaskSnapshot.ForPlan(task, version, plan.Name, request.Purpose,
                configuration.Identity, configuration.SqlTarget, configuration.Source, configuration.RemoteTarget);
            _ = BackupTaskPathFactory.Create(new BackupPlanPathInput(configuration.Identity, configuration.Source,
                configuration.RemoteTarget, request.Purpose), task.Id, command.NowUtc);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new(BackupPlanTaskCreationCode.ConfigurationUnavailable);
        }
        context.AddRange(task, snapshot);
        AddReceipt(context, command, task, reused: false);
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(BackupPlanTaskCreationCode.Created, Identity(task, snapshot));
    }, token);

    private static void AddReceipt(PlatformDbContext context, CreateBackupPlanTaskCommand command, BackupTask task, bool reused)
    {
        context.AddRange(CreateStateChange(command.Request.MutationId, task,
                reused ? task.Status : null, reused ? task.CurrentStage : null,
                reused ? ReusedReason : CreatedReason, RequestIdentity(command), command.NowUtc),
            CreateAudit(command.ActorAdminUserId, task.Id, "backup.plan.task.create",
                reused ? "reused" : "succeeded", reused ? ReusedReason : CreatedReason));
    }

    // 有版本的规范化请求只含身份及时隙；不保存安全戳、当前时钟或任何连接配置。
    private static string RequestIdentity(CreateBackupPlanTaskCommand command)
    {
        var request = command.Request;
        return string.Join('|', "plan.create.v1", request.TaskId.ToString("N"), request.PlanId.ToString("N"),
            request.PlanVersionId.ToString("N"), ((int)request.Purpose).ToString(CultureInfo.InvariantCulture),
            ((int)command.TriggerType).ToString(CultureInfo.InvariantCulture),
            request.ScheduledSlotAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "null",
            request.CoveredDifferentialSlotUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "null",
            command.ActorAdminUserId?.ToString("N") ?? "null");
    }

    private static async Task<CreatedBackupPlanTask?> ReadIdentityAsync(PlatformDbContext context, Guid taskId, CancellationToken token)
    {
        var task = await context.BackupTasks.SingleOrDefaultAsync(x => x.Id == taskId, token);
        var snapshot = await context.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == taskId, token);
        return task?.PlanId is null || snapshot?.Purpose is null ? null : Identity(task, snapshot);
    }

    private static CreatedBackupPlanTask Identity(BackupTask task, BackupTaskSnapshot snapshot) =>
        new(task.Id, task.PlanId!.Value, task.PlanVersionId!.Value, snapshot.DatabaseId, snapshot.Purpose!.Value,
            task.BackupType, task.TriggerType, task.ScheduledSlotAtUtc, task.CoveredDifferentialSlotUtc);
}
