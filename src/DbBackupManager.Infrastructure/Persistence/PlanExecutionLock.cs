using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

/// <summary>
/// 计划执行认领的全局排他锁。事务提交或回滚时释放，不覆盖单库授权。
/// </summary>
internal static class PlanExecutionLock
{
    internal const string ResourceName = "plan-execution";

    internal const int LockTimeoutMilliseconds = 15_000;

    internal static async Task<bool> TryAcquireAsync(PlatformDbContext context, CancellationToken cancellationToken)
    {
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var resource = new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = ResourceName };
        var timeout = new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMilliseconds };
        await context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', "
            + "@LockOwner = 'Transaction', @LockTimeout = @timeout",
            [result, resource, timeout],
            cancellationToken);
        return result.Value is int code && code >= 0;
    }
}
