using System.Globalization;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

// 调用方负责计划锁与事务；手动创建和组合调度共用校验、快照及原子记录。
internal static class BackupPlanTaskCreationCore
{
    private const string CreatedReason = "task.plan.created";
    private const string ReusedReason = "task.plan.creation_reused";

    internal static async Task<BackupPlanTaskCreationResult> CreateAsync(PlatformDbContext context,
        BackupPlan? plan, CreateBackupPlanTaskCommand command, bool resolveOnly, CancellationToken token)
    {
        var request = command.Request;
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
                return new(BackupPlanTaskCreationCode.AlreadyExists, identity);
            }
        }
        if (resolveOnly) return new(BackupPlanTaskCreationCode.ConcurrencyConflict);
        if (await context.BackupTasks.AnyAsync(x => x.Id == request.TaskId, token))
            return new(BackupPlanTaskCreationCode.RequestConflict);

        var version = plan.Versions.Single(x => x.Id == request.PlanVersionId);
        BackupTaskConfigurationSnapshot? configuration;
        try
        {
            configuration = await BackupTaskConfigurationReader.ReadAsync(context, plan.DatabaseId,
                version.StorageTargetId, BackupTaskPathFactory.PlanVersion, token);
        }
        catch (ArgumentException)
        {
            return new(BackupPlanTaskCreationCode.ConfigurationUnavailable);
        }
        if (configuration is null) return new(BackupPlanTaskCreationCode.ConfigurationUnavailable);
        BackupTask task;
        BackupTaskSnapshot snapshot;
        try
        {
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
        return new(BackupPlanTaskCreationCode.Created, Identity(task, snapshot));
    }

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
