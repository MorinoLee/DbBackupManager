using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

// 仅测试边界用集成身份打开本机连接；生产凭据和连接策略不变。
internal sealed class MetadataTestSessionFactory(Func<SqlConnection> createConnection) : ITargetSqlClientSessionFactory
{
    public async Task<ITargetSqlClientSession> OpenAsync(TargetSqlConnectionInput input, TargetSqlCredentialLease credential,
        CancellationToken cancellationToken)
    {
        var connection = createConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            return new SqlClientTargetSqlSession(connection);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}

internal sealed class MetadataTestCredentialResolver : ITargetSqlCredentialResolver
{
    public ValueTask<TargetSqlCredentialResolution> ResolveSqlPasswordAsync(Guid credentialReferenceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TargetSqlCredentialResolution.Succeeded(
            TargetSqlCredentialLease.CreateAndClear("synthetic-user", "synthetic-value".ToCharArray())));
    }
}
