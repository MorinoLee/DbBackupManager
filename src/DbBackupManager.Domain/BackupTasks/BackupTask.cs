using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.BackupTasks;

public sealed class BackupTask : ConcurrentEntity
{
    private BackupTask()
    {
    }

    private BackupTask(Guid id) : base(id)
    {
    }

    public BackupTask(
        Guid id,
        Guid policyId,
        BackupTaskTriggerType triggerType,
        DateTimeOffset? scheduledSlotAtUtc)
        : base(id)
    {
        PolicyId = BackupTaskValues.RequireId(policyId, nameof(policyId));
        BackupTaskValues.RequireDefined(triggerType, nameof(triggerType));
        ValidateTrigger(triggerType, scheduledSlotAtUtc);

        TriggerType = triggerType;
        ScheduledSlotAtUtc = BackupTaskValues.OptionalUtc(
            scheduledSlotAtUtc,
            nameof(scheduledSlotAtUtc));
        Status = BackupTaskStatus.Pending;
        CurrentStage = BackupTaskStage.Backup;
    }

    public static BackupTask ForPlan(
        Guid id, Guid planId, Guid planVersionId, BackupRunPurpose purpose,
        BackupTaskTriggerType triggerType, DateTimeOffset? scheduledSlotAtUtc,
        DateTimeOffset? coveredDifferentialSlotUtc = null)
    {
        BackupTaskValues.RequireDefined(triggerType, nameof(triggerType));
        ValidateTrigger(triggerType, scheduledSlotAtUtc);
        var type = BackupPlanRules.ToBackupType(purpose);
        if (purpose == BackupRunPurpose.AdHocCopyOnlyFull && triggerType != BackupTaskTriggerType.Manual)
            throw new ArgumentException("临时 COPY_ONLY 备份只能手动触发。", nameof(purpose));
        if (coveredDifferentialSlotUtc is not null && purpose != BackupRunPurpose.PlanFull)
            throw new ArgumentException("只有计划普通 FULL 可以取代 DIFF 时隙。", nameof(coveredDifferentialSlotUtc));
        return new BackupTask(id)
        {
            PlanId = BackupTaskValues.RequireId(planId, nameof(planId)),
            PlanVersionId = BackupTaskValues.RequireId(planVersionId, nameof(planVersionId)),
            BackupType = type,
            TriggerType = triggerType,
            ScheduledSlotAtUtc = BackupTaskValues.OptionalUtc(scheduledSlotAtUtc, nameof(scheduledSlotAtUtc)),
            CoveredDifferentialSlotUtc = BackupTaskValues.OptionalUtc(coveredDifferentialSlotUtc, nameof(coveredDifferentialSlotUtc)),
            Status = BackupTaskStatus.Pending,
            CurrentStage = BackupTaskStage.Backup
        };
    }

    public Guid? PolicyId { get; private set; }

    public Guid? PlanId { get; private set; }

    public Guid? PlanVersionId { get; private set; }

    public BackupType BackupType { get; private set; } = BackupType.Full;

    public DateTimeOffset? CoveredDifferentialSlotUtc { get; private set; }

    public BackupTaskTriggerType TriggerType { get; private set; }

    public DateTimeOffset? ScheduledSlotAtUtc { get; private set; }

    public BackupTaskStatus Status { get; private set; }

    public BackupTaskStage? CurrentStage { get; private set; }

    public Guid? CurrentBackupAttemptId { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset? CancellationRequestedAtUtc { get; private set; }

    public int RetryCount { get; private set; }

    public int ReconciliationAttemptCount { get; private set; }

    public DateTimeOffset? NextReconciliationAtUtc { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public BackupLeasePurpose? LeasePurpose { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseAcquiredAtUtc { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public void ClaimExecution(
        BackupStorageMode storageMode,
        Guid backupAttemptId,
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var currentState = State();
        var claimed = BackupTaskStateMachine.Claim(currentState, storageMode);
        var validatedAttemptId = BackupTaskValues.RequireId(
            backupAttemptId,
            nameof(backupAttemptId));

        if (CurrentStage == BackupTaskStage.Backup
            && CurrentBackupAttemptId == validatedAttemptId)
        {
            throw new InvalidOperationException("重新执行 Backup 必须创建新的 Attempt。");
        }

        if (CurrentStage != BackupTaskStage.Backup
            && CurrentBackupAttemptId != validatedAttemptId)
        {
            throw new InvalidOperationException("后续阶段必须继续使用当前 Attempt。");
        }

        SetLease(
            BackupLeasePurpose.Execution,
            leaseToken,
            leaseOwner,
            acquiredAtUtc,
            expiresAtUtc);
        CurrentBackupAttemptId = validatedAttemptId;
        SetState(claimed);
        StartedAtUtc ??= BackupTaskValues.RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        CompletedAtUtc = null;
        ClearReconciliationSchedule();
        ClearError();
    }

    public void AcquireReconciliationLease(
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (Status != BackupTaskStatus.NeedsAttention)
        {
            throw new InvalidOperationException("只有待核对任务可以取得核对租约。");
        }

        if (CurrentBackupAttemptId is null)
        {
            throw new InvalidOperationException("待核对任务必须关联当前 Attempt。");
        }

        var acquiredAt = BackupTaskValues.RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        if (NextReconciliationAtUtc is null || NextReconciliationAtUtc > acquiredAt)
        {
            throw new InvalidOperationException("任务尚未到达下一次允许核对时间。");
        }

        if (LeaseToken is not null)
        {
            if (LeasePurpose != BackupLeasePurpose.Reconciliation
                || LeaseExpiresAtUtc is null
                || LeaseExpiresAtUtc > acquiredAt)
            {
                throw new InvalidOperationException("任务已经持有未过期的核对租约。");
            }

            ClearLease();
        }

        SetLease(
            BackupLeasePurpose.Reconciliation,
            leaseToken,
            leaseOwner,
            acquiredAt,
            expiresAtUtc);
    }

    public void RequestReconciliation(DateTimeOffset requestedAtUtc)
    {
        var requestedAt = BackupTaskValues.RequireUtc(requestedAtUtc, nameof(requestedAtUtc));
        if (Status != BackupTaskStatus.NeedsAttention || NextReconciliationAtUtc is null)
        {
            throw new InvalidOperationException("只有待核对任务可以请求重新核对。");
        }

        if (LeaseToken is not null
            && (LeaseExpiresAtUtc is null || LeaseExpiresAtUtc > requestedAt))
        {
            throw new InvalidOperationException("任务正在核对，不能覆盖活动租约。");
        }

        NextReconciliationAtUtc = requestedAt;
    }

    public void ConfirmFailed(
        BackupStorageMode storageMode,
        DateTimeOffset utcNow,
        string errorCode,
        string errorMessage)
    {
        ConfirmTerminal(
            storageMode,
            utcNow,
            BackupReconciliationOutcome.Failed,
            errorCode,
            errorMessage);
    }

    public void ConfirmCancelled(BackupStorageMode storageMode, DateTimeOffset utcNow)
    {
        ConfirmTerminal(
            storageMode,
            utcNow,
            BackupReconciliationOutcome.Cancelled,
            null,
            null);
    }

    public void RenewLease(
        Guid leaseToken,
        BackupLeasePurpose purpose,
        DateTimeOffset utcNow,
        DateTimeOffset expiresAtUtc)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        var expiry = BackupTaskValues.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        EnsureLease(leaseToken, purpose, now);

        if (expiry <= now || expiry <= LeaseExpiresAtUtc)
        {
            throw new ArgumentException("续租到期时间必须晚于当前时间和原到期时间。", nameof(expiresAtUtc));
        }

        LeaseExpiresAtUtc = expiry;
    }

    public void RequestCancellation(DateTimeOffset requestedAtUtc, BackupStorageMode storageMode)
    {
        var requestedAt = BackupTaskValues.RequireUtc(requestedAtUtc, nameof(requestedAtUtc));

        if (Status == BackupTaskStatus.Pending)
        {
            SetState(BackupTaskStateMachine.CancelPending(State(), storageMode));
            CancellationRequestedAtUtc = requestedAt;
            CompletedAtUtc = requestedAt;
            ClearReconciliationSchedule();
            ClearLease();
            return;
        }

        if (Status is BackupTaskStatus.Running or BackupTaskStatus.NeedsAttention)
        {
            CancellationRequestedAtUtc ??= requestedAt;
            return;
        }

        throw new InvalidOperationException("当前任务状态不接受取消请求。");
    }

    public void CancelRunningAtSafeBoundary(
        BackupStorageMode storageMode,
        Guid leaseToken,
        DateTimeOffset utcNow)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        EnsureLease(leaseToken, BackupLeasePurpose.Execution, now);
        if (CancellationRequestedAtUtc is null)
        {
            throw new InvalidOperationException("尚未请求取消，不能提交安全边界取消。");
        }

        SetState(BackupTaskStateMachine.CancelRunningAtSafeBoundary(State(), storageMode));
        CompletedAtUtc = now;
        ClearReconciliationSchedule();
        ClearError();
        ClearLease();
    }

    public void CompleteRunningStage(
        BackupStorageMode storageMode,
        Guid leaseToken,
        DateTimeOffset utcNow)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        EnsureLease(leaseToken, BackupLeasePurpose.Execution, now);
        var next = BackupTaskStateMachine.CompleteRunningStage(State(), storageMode);

        SetState(next);
        ClearReconciliationSchedule();
        ClearError();
        if (next.Status == BackupTaskStatus.Succeeded)
        {
            CompletedAtUtc = now;
            ClearLease();
        }
    }

    public void RejectPendingBackup(
        BackupStorageMode storageMode,
        DateTimeOffset utcNow,
        string errorCode,
        string errorMessage)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        var error = ValidateError(errorCode, errorMessage);
        var rejected = BackupTaskStateMachine.RejectPendingBackup(State(), storageMode);
        if (LeaseToken is not null)
        {
            throw new InvalidOperationException("持有租约的任务不能记录认领前准备失败。");
        }

        SetState(rejected);
        ErrorCode = error.Code;
        ErrorMessage = error.Message;
        CompletedAtUtc = now;
        ClearReconciliationSchedule();
    }

    public void RecordConfirmedFailure(
        BackupStorageMode storageMode,
        Guid leaseToken,
        DateTimeOffset utcNow,
        string errorCode,
        string errorMessage)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        EnsureLease(leaseToken, BackupLeasePurpose.Execution, now);
        var error = ValidateError(errorCode, errorMessage);

        SetState(BackupTaskStateMachine.RecordConfirmedFailure(State(), storageMode));
        ErrorCode = error.Code;
        ErrorMessage = error.Message;
        CompletedAtUtc = now;
        ClearReconciliationSchedule();
        ClearLease();
    }

    public void RecordIndeterminateResult(
        BackupStorageMode storageMode,
        Guid leaseToken,
        DateTimeOffset utcNow,
        string errorCode,
        string errorMessage)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        EnsureLease(leaseToken, BackupLeasePurpose.Execution, now);
        var error = ValidateError(errorCode, errorMessage);

        SetState(BackupTaskStateMachine.RecordIndeterminateResult(State(), storageMode));
        ErrorCode = error.Code;
        ErrorMessage = error.Message;
        CompletedAtUtc = null;
        ScheduleInitialReconciliation(now);
        ClearLease();
    }

    public void RecordExpiredExecutionLease(
        BackupStorageMode storageMode,
        DateTimeOffset utcNow,
        string errorCode,
        string errorMessage)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        if (LeasePurpose != BackupLeasePurpose.Execution
            || LeaseExpiresAtUtc is null
            || LeaseExpiresAtUtc > now)
        {
            throw new InvalidOperationException("执行租约尚未到期或不存在。");
        }

        var error = ValidateError(errorCode, errorMessage);
        SetState(BackupTaskStateMachine.RecordExpiredLease(State(), storageMode));
        ErrorCode = error.Code;
        ErrorMessage = error.Message;
        ScheduleInitialReconciliation(now);
        ClearLease();
    }

    public BackupAttemptHandling RetryFailed(
        BackupStorageMode storageMode,
        DateTimeOffset requestedAtUtc)
    {
        _ = BackupTaskValues.RequireUtc(requestedAtUtc, nameof(requestedAtUtc));
        var transition = BackupTaskStateMachine.RetryFailed(State(), storageMode);

        checked
        {
            RetryCount++;
        }

        SetState(transition.State);
        CompletedAtUtc = null;
        CancellationRequestedAtUtc = null;
        ClearReconciliationSchedule();
        ClearError();
        ClearLease();
        return transition.AttemptHandling;
    }

    public BackupAttemptHandling? CompleteReconciliation(
        BackupStorageMode storageMode,
        Guid leaseToken,
        DateTimeOffset utcNow,
        BackupReconciliationOutcome outcome,
        string? errorCode = null,
        string? errorMessage = null)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        BackupTaskValues.RequireDefined(outcome, nameof(outcome));
        EnsureLease(leaseToken, BackupLeasePurpose.Reconciliation, now);

        BackupAttemptHandling? attemptHandling = null;
        switch (outcome)
        {
            case BackupReconciliationOutcome.Succeeded:
                SetState(BackupTaskStateMachine.ReconcileSucceeded(State(), storageMode));
                CompletedAtUtc = Status == BackupTaskStatus.Succeeded ? now : null;
                ClearReconciliationSchedule();
                ClearError();
                break;

            case BackupReconciliationOutcome.SafeToRetry:
                var transition = BackupTaskStateMachine.ReconcileSafeToRetry(State(), storageMode);
                SetState(transition.State);
                attemptHandling = transition.AttemptHandling;
                CompletedAtUtc = null;
                ClearReconciliationSchedule();
                ClearError();
                break;

            case BackupReconciliationOutcome.Failed:
                var failure = ValidateError(errorCode!, errorMessage!);
                SetState(BackupTaskStateMachine.ReconcileFailed(State(), storageMode));
                ErrorCode = failure.Code;
                ErrorMessage = failure.Message;
                CompletedAtUtc = now;
                ClearReconciliationSchedule();
                break;

            case BackupReconciliationOutcome.Cancelled:
                SetState(BackupTaskStateMachine.ReconcileCancelled(State(), storageMode));
                CancellationRequestedAtUtc ??= now;
                CompletedAtUtc = now;
                ClearReconciliationSchedule();
                ClearError();
                break;

            case BackupReconciliationOutcome.Inconclusive:
                var inconclusive = ValidateError(errorCode!, errorMessage!);
                SetState(BackupTaskStateMachine.ReconcileInconclusive(State(), storageMode));
                ErrorCode = inconclusive.Code;
                ErrorMessage = inconclusive.Message;
                CompletedAtUtc = null;
                ScheduleNextReconciliation(now);
                break;
        }

        ClearLease();
        return attemptHandling;
    }

    private void ConfirmTerminal(
        BackupStorageMode storageMode,
        DateTimeOffset utcNow,
        BackupReconciliationOutcome outcome,
        string? errorCode,
        string? errorMessage)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        BackupTaskValues.RequireDefined(storageMode, nameof(storageMode));
        BackupTaskValues.RequireDefined(outcome, nameof(outcome));
        if (LeaseToken is not null
            && (LeaseExpiresAtUtc is null || LeaseExpiresAtUtc > now))
        {
            throw new InvalidOperationException("任务正在执行或核对，不能由管理员确认终态。");
        }

        if (LeaseToken is not null)
        {
            ClearLease();
        }

        switch (outcome)
        {
            case BackupReconciliationOutcome.Failed:
                var failure = ValidateError(errorCode!, errorMessage!);
                SetState(BackupTaskStateMachine.ReconcileFailed(State(), storageMode));
                ErrorCode = failure.Code;
                ErrorMessage = failure.Message;
                CompletedAtUtc = now;
                ClearReconciliationSchedule();
                break;
            case BackupReconciliationOutcome.Cancelled:
                SetState(BackupTaskStateMachine.ReconcileCancelled(State(), storageMode));
                CancellationRequestedAtUtc ??= now;
                CompletedAtUtc = now;
                ClearReconciliationSchedule();
                ClearError();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private static void ValidateTrigger(
        BackupTaskTriggerType triggerType,
        DateTimeOffset? scheduledSlotAtUtc)
    {
        if (triggerType == BackupTaskTriggerType.Scheduled && scheduledSlotAtUtc is null)
        {
            throw new ArgumentException("计划任务必须保存计划时隙。", nameof(scheduledSlotAtUtc));
        }

        if (triggerType == BackupTaskTriggerType.Manual && scheduledSlotAtUtc is not null)
        {
            throw new ArgumentException("手动任务不能保存计划时隙。", nameof(scheduledSlotAtUtc));
        }
    }

    private void SetLease(
        BackupLeasePurpose purpose,
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (LeaseToken is not null)
        {
            throw new InvalidOperationException("任务已经持有租约。");
        }

        BackupTaskValues.RequireDefined(purpose, nameof(purpose));
        var token = BackupTaskValues.RequireId(leaseToken, nameof(leaseToken));
        var owner = BackupTaskValues.RequireText(leaseOwner, 200, nameof(leaseOwner));
        var acquired = BackupTaskValues.RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        var expires = BackupTaskValues.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (expires <= acquired)
        {
            throw new ArgumentException("租约到期时间必须晚于取得时间。", nameof(expiresAtUtc));
        }

        LeasePurpose = purpose;
        LeaseToken = token;
        LeaseOwner = owner;
        LeaseAcquiredAtUtc = acquired;
        LeaseExpiresAtUtc = expires;
    }

    private void EnsureLease(
        Guid leaseToken,
        BackupLeasePurpose purpose,
        DateTimeOffset utcNow)
    {
        BackupTaskValues.RequireDefined(purpose, nameof(purpose));
        var token = BackupTaskValues.RequireId(leaseToken, nameof(leaseToken));

        if (LeaseToken != token || LeasePurpose != purpose)
        {
            throw new InvalidOperationException("租约能力不匹配。");
        }

        if (LeaseExpiresAtUtc is null || LeaseExpiresAtUtc <= utcNow)
        {
            throw new InvalidOperationException("租约已经到期。");
        }
    }

    private void SetState(BackupTaskExecutionState state)
    {
        Status = state.Status;
        CurrentStage = state.CurrentStage;
    }

    private BackupTaskExecutionState State()
    {
        return new BackupTaskExecutionState(Status, CurrentStage);
    }

    private static (string Code, string Message) ValidateError(
        string errorCode,
        string errorMessage)
    {
        return (
            BackupTaskValues.RequireText(errorCode, 100, nameof(errorCode)),
            BackupTaskValues.RequireText(errorMessage, 500, nameof(errorMessage)));
    }

    private void ClearError()
    {
        ErrorCode = null;
        ErrorMessage = null;
    }

    private void ScheduleInitialReconciliation(DateTimeOffset utcNow)
    {
        ReconciliationAttemptCount = 0;
        NextReconciliationAtUtc = utcNow;
    }

    private void ScheduleNextReconciliation(DateTimeOffset utcNow)
    {
        if (ReconciliationAttemptCount < int.MaxValue)
        {
            ReconciliationAttemptCount++;
        }

        var delay = ReconciliationAttemptCount switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromHours(1),
        };
        NextReconciliationAtUtc = utcNow + delay;
    }

    private void ClearReconciliationSchedule()
    {
        ReconciliationAttemptCount = 0;
        NextReconciliationAtUtc = null;
    }

    private void ClearLease()
    {
        LeasePurpose = null;
        LeaseToken = null;
        LeaseOwner = null;
        LeaseAcquiredAtUtc = null;
        LeaseExpiresAtUtc = null;
    }
}
