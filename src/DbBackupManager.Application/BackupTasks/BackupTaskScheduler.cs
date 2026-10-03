using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public sealed class BackupTaskScheduler(
    ISchedulableBackupPolicyReader reader,
    IBackupTaskExecutionStore store,
    TimeProvider clock) : IBackupTaskScheduler
{
    public async Task<BackupScheduleCycleResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.GetUtcNow().ToUniversalTime();
        var policies = await reader.ListAsync(cancellationToken);
        var results = new List<BackupSchedulePolicyResult>(policies.Count);
        foreach (var policy in policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await SchedulePolicyAsync(policy, nowUtc, cancellationToken));
        }

        return new BackupScheduleCycleResult(results);
    }

    private async Task<BackupSchedulePolicyResult> SchedulePolicyAsync(
        SchedulableBackupPolicy policy,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var calculation = BackupScheduleCalculator.Calculate(new BackupScheduleRequest(
                policy.ScheduleType,
                policy.LocalTime,
                policy.DaysOfWeek,
                policy.TimeZoneId,
                policy.ScheduleEffectiveFromUtc,
                nowUtc));
            if (calculation.Status == BackupScheduleCalculationStatus.InvalidTimeZone)
            {
                return new BackupSchedulePolicyResult(policy.PolicyId, BackupSchedulePolicyResultCode.InvalidTimeZone);
            }

            if (calculation.LatestDueSlotUtc is not { } slot)
            {
                return new BackupSchedulePolicyResult(policy.PolicyId, BackupSchedulePolicyResultCode.NotDue);
            }

            var slotUtc = slot.ToUniversalTime();
            var created = await store.CreateTaskAsync(
                new CreateBackupTaskCommand(
                    Guid.NewGuid(),
                    policy.PolicyId,
                    BackupTaskTriggerType.Scheduled,
                    slotUtc,
                    Guid.NewGuid(),
                    nowUtc),
                cancellationToken);
            return created.Code switch
            {
                BackupTaskStoreResultCode.Succeeded => new BackupSchedulePolicyResult(
                    policy.PolicyId,
                    BackupSchedulePolicyResultCode.Created,
                    slotUtc,
                    created.Value?.TaskId),
                BackupTaskStoreResultCode.AlreadyExists or BackupTaskStoreResultCode.AlreadyApplied =>
                    new BackupSchedulePolicyResult(
                        policy.PolicyId,
                        BackupSchedulePolicyResultCode.AlreadyExists,
                        slotUtc,
                        created.Value?.TaskId),
                BackupTaskStoreResultCode.ConfigurationUnavailable => new BackupSchedulePolicyResult(
                    policy.PolicyId,
                    BackupSchedulePolicyResultCode.ConfigurationUnavailable,
                    slotUtc),
                // 计划创建无管理员会话。状态错配、身份与并发冲突只隔离本策略，不升级为整轮 Platform DB 失败。
                BackupTaskStoreResultCode.StateMismatch
                    or BackupTaskStoreResultCode.AuthenticationRequired
                    or BackupTaskStoreResultCode.ConcurrencyConflict =>
                    new BackupSchedulePolicyResult(
                        policy.PolicyId,
                        BackupSchedulePolicyResultCode.CreateFailed,
                        slotUtc),
                _ => new BackupSchedulePolicyResult(
                    policy.PolicyId,
                    BackupSchedulePolicyResultCode.CreateFailed,
                    slotUtc)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsPolicyFailure(exception))
        {
            return new BackupSchedulePolicyResult(policy.PolicyId, BackupSchedulePolicyResultCode.CreateFailed);
        }
    }

    private static bool IsPolicyFailure(Exception exception) =>
        exception is ArgumentException or InvalidOperationException;
}
