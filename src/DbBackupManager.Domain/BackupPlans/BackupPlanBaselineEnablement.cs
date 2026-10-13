using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.BackupPlans;

/// <summary>一次启用事件只记录一行。排队时关联那一份计划内普通 FULL，跳过时不建任务。</summary>
public sealed class BackupPlanBaselineEnablement : ConcurrentEntity
{
    private BackupPlanBaselineEnablement()
    {
    }

    public BackupPlanBaselineEnablement(
        Guid id,
        Guid planId,
        Guid planVersionId,
        Guid mutationId,
        Guid? taskId,
        BackupPlanBaselineEnablementDecision decision)
        : base(id)
    {
        if (planId == Guid.Empty || planVersionId == Guid.Empty || mutationId == Guid.Empty)
        {
            throw new ArgumentException("启用事件的计划、版本和 MutationId 不能为空。");
        }

        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "枚举值无效。");
        }

        if (decision == BackupPlanBaselineEnablementDecision.Queued)
        {
            if (taskId is null || taskId == Guid.Empty)
            {
                throw new ArgumentException("排队补基线必须关联任务。", nameof(taskId));
            }
        }
        else if (taskId is not null)
        {
            throw new ArgumentException("未排队的启用事件不能关联任务。", nameof(taskId));
        }

        PlanId = planId;
        PlanVersionId = planVersionId;
        MutationId = mutationId;
        TaskId = taskId;
        Decision = decision;
    }

    public Guid PlanId { get; private set; }

    public Guid PlanVersionId { get; private set; }

    public Guid MutationId { get; private set; }

    public Guid? TaskId { get; private set; }

    public BackupPlanBaselineEnablementDecision Decision { get; private set; }
}
