using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

/// <summary>
/// 删除领取与新备份登记共用的协调锁。会话锁只在物理删除期间由独立连接持有，
/// 不把平台 Context 或事务留在文件 I/O 上。
/// </summary>
internal static class PlanRetentionLock
{
    /// <summary>
    /// 计划副本物理删除的调用超时是 60 秒。协调锁必须盖过这次等待和随后的提交，
    /// 避免登记或阶段提交在删除尚未结束时得到 DatabaseBlocked。
    /// </summary>
    internal const int LockTimeoutMilliseconds = 90_000;

    internal static string Name(Guid databaseId) => $"plan-retention-{databaseId:N}";

    internal static async Task<bool> TryAcquireAsync(
        PlatformDbContext context,
        Guid databaseId,
        CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var wait = timeoutMilliseconds ?? LockTimeoutMilliseconds;
        if (wait < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds), "锁等待不能为负。");
        }

        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var resource = new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = Name(databaseId) };
        var timeout = new SqlParameter("@timeout", SqlDbType.Int) { Value = wait };
        await context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', "
            + "@LockOwner = 'Transaction', @LockTimeout = @timeout",
            [result, resource, timeout],
            cancellationToken);
        return result.Value is int code && code >= 0;
    }
}
