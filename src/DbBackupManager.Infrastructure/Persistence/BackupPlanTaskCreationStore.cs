using System.Data;
using DbBackupManager.Application.BackupTasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupPlanTaskCreationStore(IDbContextFactory<PlatformDbContext> contextFactory)
    : IBackupPlanTaskCreationStore
{
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
        var result = await BackupPlanTaskCreationCore.CreateAsync(context, plan, command, resolveOnly, token);
        if (result.Code is BackupPlanTaskCreationCode.Created or BackupPlanTaskCreationCode.AlreadyExists)
            await transaction.CommitAsync(token);
        return result;
    }, token);
}
