using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupArtifactDeletionGuard(IDbContextFactory<PlatformDbContext> factory)
    : IBackupArtifactDeletionGuard
{
    public async Task<BackupArtifactDeletionDecision> EvaluateAsync(BackupFileDeleteRequest request, CancellationToken cancellationToken = default)
    {
        var owner = request.Owner;
        if (owner is null) return new(false, "artifact.owner_unknown");
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var task = await context.BackupTasks.SingleOrDefaultAsync(x => x.Id == owner.TaskId, cancellationToken);
        var snapshot = await context.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == owner.TaskId, cancellationToken);
        if (task is null || snapshot is null) return new(false, "artifact.owner_unknown");
        // 计划副本保持受保护。旧策略身份已经移除，缺少计划标识的对象也不再放行删除。
        if (task.PlanId is not null || snapshot.Purpose is not null || snapshot.FileNameRuleVersion == "v3")
            return new(false, "plan.copy.protected");
        return new(false, "artifact.owner_unknown");
    }
}
