using System.Data;
using System.Security.Cryptography;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class PlanCopyDeletionCoordinator(
    IDbContextFactory<PlatformDbContext> factory,
    TimeProvider clock,
    ILogger<PlanCopyDeletionCoordinator>? logger = null)
    : IPlanCopyDeletionCoordinator
{
    private static readonly Action<ILogger, Guid, string, Exception?> ReceiptStatusMismatch = LoggerMessage.Define<Guid, string>(
        LogLevel.Warning,
        new EventId(6403, "PlanCopyDeletionReceiptStatusMismatch"),
        "删除回执与副本状态不一致，已按未知处理。副本：{FileId} 状态：{Status}");

    public async Task<IPlanCopyDeletionPermit> OpenAsync(
        BackupFileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Owner is not { Role: BackupArtifactPathRole.RegisteredFile, FileId: { } fileId })
        {
            return PlanCopyDeletionPermit.NotApplicable();
        }

        await using var lookup = await factory.CreateDbContextAsync(cancellationToken);
        var file = await lookup.BackupFiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == fileId, cancellationToken);
        var task = file is null
            ? null
            : await lookup.BackupTasks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == file.TaskId, cancellationToken);
        var snapshot = file is null
            ? null
            : await lookup.BackupTaskSnapshots.AsNoTracking().SingleOrDefaultAsync(item => item.TaskId == file.TaskId, cancellationToken);
        if (file is null || task is null || snapshot is null || !PlanLocationRetentionWork.IsPlanCopy(file, task, snapshot))
        {
            return PlanCopyDeletionPermit.NotApplicable();
        }

        var connectionString = lookup.Database.GetConnectionString()
            ?? throw new InvalidOperationException("平台库连接字符串不可用。");
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await AcquireSessionAsync(connection, file.DatabaseId, cancellationToken))
        {
            await connection.DisposeAsync();
            return PlanCopyDeletionPermit.Denied(PlanCopyDeletionReasons.Busy);
        }

        try
        {
            // 生产开启了 EnableRetryOnFailure，显式事务必须整体交给执行策略执行，否则 EF 会拒绝
            // 事务内的查询。会话锁在策略之外持有，重试整段事务时不会重复取锁或重复放开。
            await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            var permit = await strategy.ExecuteAsync(async () =>
            {
                await using var context = await factory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                await BackupInvocationAuthorizationStore.LockDatabaseAsync(context, file.DatabaseId, cancellationToken);
                var current = await context.BackupFiles.AsTracking().SingleAsync(item => item.Id == file.Id, cancellationToken);
                var now = clock.GetUtcNow();
                var endpoint = await BackupFileRetentionStore.TryCreateEndpointAsync(context, current, true, cancellationToken);
                var leaseMatches = current.Status == BackupFileStatus.DeletePending
                    && current.DeletionLeaseToken == request.Owner.LeaseToken
                    && current.DeletionLeaseExpiresAtUtc > now
                    && current.RowVersion.SequenceEqual(request.Owner.RowVersion)
                    && current.Path == request.Path
                    && current.LengthBytes == request.ExpectedLengthBytes
                    && endpoint is not null
                    && SameEndpoint(endpoint, request.Endpoint);
                var decisions = PlanLocationRetentionWork.Decisions(
                    await PlanLocationRetentionWork.LoadAsync(context, current.DatabaseId, now, cancellationToken));
                var deletable = leaseMatches
                    && decisions.TryGetValue(current.Id, out var decision)
                    && PlanLocationRecovery.RetainsPhysicalLease(decision);
                if (!deletable)
                {
                    if (leaseMatches)
                    {
                        current.CancelDeletionLease(now);
                        context.BackupFileStateChanges.Add(new BackupFileStateChange(
                            Guid.NewGuid(),
                            current.Id,
                            current.TaskId,
                            BackupFileStatus.DeletePending,
                            BackupFileStatus.Available,
                            PlanLocationRetentionWork.ReleasedReason,
                            now));
                        await context.SaveChangesAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                    return PlanCopyDeletionPermit.Denied(leaseMatches ? "plan.copy.protected" : "artifact.owner_unknown");
                }

                await transaction.CommitAsync(cancellationToken);
                return new PlanCopyDeletionPermit(
                    factory, connection, file.DatabaseId, current.Id, request.Owner.LeaseToken, clock, allowed: true, null, logger);
            });

            if (!permit.Allowed)
            {
                await ReleaseSessionAsync(connection, file.DatabaseId);
                await connection.DisposeAsync();
            }

            return permit;
        }
        catch
        {
            await ReleaseSessionAsync(connection, file.DatabaseId);
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// 本次删除操作的确定性回执身份。由文件与删除租约推导，同一操作的每次重放得到同一标识；
    /// 不同操作（不同文件或不同租约）不会碰撞，因此不能把别的 Deleted 副本当成本次成功。
    /// </summary>
    private static Guid ConfirmedUnlinkReceipt(Guid fileId, Guid leaseToken)
    {
        var bytes = new byte[32];
        fileId.TryWriteBytes(bytes.AsSpan(0, 16));
        leaseToken.TryWriteBytes(bytes.AsSpan(16, 16));
        return new Guid(SHA256.HashData(bytes).AsSpan(0, 16));
    }

    private static bool SameEndpoint(BackupFileEndpointInput left, BackupFileEndpointInput right) =>
        left.Protocol == right.Protocol
        && left.Host == right.Host
        && left.Port == right.Port
        && left.RootPath == right.RootPath
        && left.CredentialReferenceId == right.CredentialReferenceId
        && left.SftpHostKeyFingerprint == right.SftpHostKeyFingerprint;

    private static async Task<bool> AcquireSessionAsync(SqlConnection connection, Guid databaseId, CancellationToken cancellationToken)
    {
        var result = await ExecuteLockAsync(connection, databaseId, acquire: true, cancellationToken);
        return result >= 0;
    }

    private static async Task ReleaseSessionAsync(SqlConnection connection, Guid databaseId)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await ExecuteLockAsync(connection, databaseId, acquire: false, CancellationToken.None);
        }
        catch (SqlException)
        {
            // 连接关闭时会话锁会一并释放。
        }
    }

    private static async Task<int> ExecuteLockAsync(
        SqlConnection connection,
        Guid databaseId,
        bool acquire,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = acquire
            ? "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout"
            : "EXEC @result = sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'";
        var result = command.Parameters.Add("@result", SqlDbType.Int);
        result.Direction = ParameterDirection.Output;
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = PlanRetentionLock.Name(databaseId);
        if (acquire)
        {
            command.Parameters.Add("@timeout", SqlDbType.Int).Value = PlanRetentionLock.LockTimeoutMilliseconds;
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result.Value is int code ? code : -999;
    }

    private sealed class PlanCopyDeletionPermit : IPlanCopyDeletionPermit
    {
        private readonly IDbContextFactory<PlatformDbContext>? _factory;
        private readonly TimeProvider? _clock;
        private SqlConnection? _connection;
        private readonly Guid _databaseId;
        private readonly Guid _fileId;
        private readonly Guid _leaseToken;
        private readonly ILogger? _logger;
        private bool _disposed;

        private PlanCopyDeletionPermit(bool applies, bool allowed, string? reason)
        {
            Applies = applies;
            Allowed = allowed;
            ReasonCode = reason;
        }

        public PlanCopyDeletionPermit(
            IDbContextFactory<PlatformDbContext> factory,
            SqlConnection connection,
            Guid databaseId,
            Guid fileId,
            Guid leaseToken,
            TimeProvider clock,
            bool allowed,
            string? reason,
            ILogger? logger)
        {
            _factory = factory;
            _connection = connection;
            _databaseId = databaseId;
            _fileId = fileId;
            _leaseToken = leaseToken;
            _clock = clock;
            _logger = logger;
            Applies = true;
            Allowed = allowed;
            ReasonCode = reason;
        }

        public bool Applies { get; }

        public bool Allowed { get; }

        public string? ReasonCode { get; }

        public static PlanCopyDeletionPermit NotApplicable() => new(false, false, null);

        public static PlanCopyDeletionPermit Denied(string reason) => new(true, false, reason);

        public async Task<bool> CommitDeletedAsync(CancellationToken cancellationToken = default)
        {
            if (!Allowed || _factory is null || _clock is null || _disposed)
            {
                return false;
            }

            // 物理文件已经解开。提交不再跟随调用方取消，也不因租约刚到期或决策变化改回可用。
            // 这一步只重放平台事务；物理删除已在闭包之外完成，不会因为重试再删一次。
            _ = cancellationToken;
            var now = _clock.GetUtcNow();
            await using var strategyContext = await _factory.CreateDbContextAsync(CancellationToken.None);
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            var committed = await strategy.ExecuteAsync(async () =>
            {
                await using var context = await _factory.CreateDbContextAsync(CancellationToken.None);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None);
                await BackupInvocationAuthorizationStore.LockDatabaseAsync(context, _databaseId, CancellationToken.None);
                var file = await context.BackupFiles.AsTracking().SingleOrDefaultAsync(item => item.Id == _fileId, CancellationToken.None);
                if (file is null)
                {
                    await transaction.CommitAsync(CancellationToken.None);
                    return false;
                }

                // 幂等收口：COMMIT 实际成功后响应可能丢失，执行策略会重放这段平台事务。此时副本已是
                // Deleted、删除租约也已清空，不能把本次已提交的成功判成状态不匹配。用本次删除操作的
                // 确定性 MutationId 作为稳定回执身份：回执存在即本次已应用，直接返回成功。
                // 只有"回执存在且副本已 Deleted"才算本次成功；任意 Deleted 副本或缺回执都不算。
                var receiptId = ConfirmedUnlinkReceipt(file.Id, _leaseToken);
                var applied = await context.BackupFileStateChanges.AsNoTracking()
                    .AnyAsync(item => item.MutationId == receiptId, CancellationToken.None);
                if (applied)
                {
                    if (file.Status != BackupFileStatus.Deleted)
                    {
                        // 回执声称已确认解开，副本却不是 Deleted：状态不一致，交回调用方按未知处理。
                        if (_logger is not null)
                        {
                            ReceiptStatusMismatch(_logger, file.Id, file.Status.ToString(), null);
                        }

                        await transaction.CommitAsync(CancellationToken.None);
                        return false;
                    }

                    await transaction.CommitAsync(CancellationToken.None);
                    return true;
                }

                if (file.Status != BackupFileStatus.DeletePending || file.DeletionLeaseToken != _leaseToken)
                {
                    await transaction.CommitAsync(CancellationToken.None);
                    return false;
                }

                file.RecordConfirmedUnlink(_leaseToken, now);
                await PlanLocationRetentionWork.NoteStagingCleanedAsync(context, file, now, CancellationToken.None);
                context.BackupFileStateChanges.Add(new BackupFileStateChange(
                    receiptId,
                    file.Id,
                    file.TaskId,
                    BackupFileStatus.DeletePending,
                    BackupFileStatus.Deleted,
                    PlanLocationRetentionWork.DeletedReason,
                    now));
                context.AuditRecords.Add(new(null, "backup.file.plan_retention.commit", "BackupFile", file.Id.ToString("N"), "succeeded", PlanLocationRetentionWork.DeletedReason));
                await context.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
                return true;
            });

            // 会话锁与投影用的是同一把保留锁。删除已经提交后再放开，这次写入口才能补做待投影。
            if (_connection is not null)
            {
                await ReleaseSessionAsync(_connection, _databaseId);
            }

            await RemoteGapNotices.TryDrainPendingAsync(_factory, _databaseId, CancellationToken.None, _logger);
            return committed;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_connection is not null)
            {
                await ReleaseSessionAsync(_connection, _databaseId);
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
    }
}

