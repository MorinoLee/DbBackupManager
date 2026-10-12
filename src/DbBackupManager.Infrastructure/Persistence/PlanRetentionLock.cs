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
    internal static string Name(Guid databaseId) => $"plan-retention-{databaseId:N}";

    internal static async Task<bool> TryAcquireAsync(
        PlatformDbContext context,
        Guid databaseId,
        CancellationToken cancellationToken)
    {
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var resource = new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = Name(databaseId) };
        await context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', "
            + "@LockOwner = 'Transaction', @LockTimeout = 15000",
            [result, resource],
            cancellationToken);
        return result.Value is int code && code >= 0;
    }
}
