using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class PlanCopyDeletionCoordinator(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock)
    : IPlanCopyDeletionCoordinator
{
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
        if (file is null || task is null || snapshot is null
            || task.PlanId is null && snapshot.Purpose is null && snapshot.FileNameRuleVersion != "v3" && file.BackupSetId is null)
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
            return PlanCopyDeletionPermit.Denied("plan.copy.protected");
        }

        try
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
                && PlanLocationRecovery.IsDeletable(decision);
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
                await ReleaseSessionAsync(connection, file.DatabaseId);
                await connection.DisposeAsync();
                return PlanCopyDeletionPermit.Denied(leaseMatches ? "plan.copy.protected" : "artifact.owner_unknown");
            }

            await transaction.CommitAsync(cancellationToken);
            return new PlanCopyDeletionPermit(factory, connection, file.DatabaseId, current.Id, request.Owner.LeaseToken, clock, allowed: true, null);
        }
        catch
        {
            await ReleaseSessionAsync(connection, file.DatabaseId);
            await connection.DisposeAsync();
            throw;
        }
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
            ? "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 15000"
            : "EXEC @result = sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'";
        var result = command.Parameters.Add("@result", SqlDbType.Int);
        result.Direction = ParameterDirection.Output;
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = PlanRetentionLock.Name(databaseId);
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
            string? reason)
        {
            _factory = factory;
            _connection = connection;
            _databaseId = databaseId;
            _fileId = fileId;
            _leaseToken = leaseToken;
            _clock = clock;
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

            var now = _clock.GetUtcNow();
            await using var context = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await BackupInvocationAuthorizationStore.LockDatabaseAsync(context, _databaseId, cancellationToken);
            var file = await context.BackupFiles.AsTracking().SingleOrDefaultAsync(item => item.Id == _fileId, cancellationToken);
            if (file is null
                || file.Status != BackupFileStatus.DeletePending
                || file.DeletionLeaseToken != _leaseToken
                || file.DeletionLeaseExpiresAtUtc is null
                || file.DeletionLeaseExpiresAtUtc <= now)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            var decisions = PlanLocationRetentionWork.Decisions(
                await PlanLocationRetentionWork.LoadAsync(context, _databaseId, now, cancellationToken));
            if (!decisions.TryGetValue(file.Id, out var decision) || !PlanLocationRecovery.IsDeletable(decision))
            {
                file.CancelDeletionLease(now);
                context.BackupFileStateChanges.Add(new BackupFileStateChange(
                    Guid.NewGuid(),
                    file.Id,
                    file.TaskId,
                    BackupFileStatus.DeletePending,
                    BackupFileStatus.Available,
                    PlanLocationRetentionWork.ReleasedReason,
                    now));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            file.RecordDeleted(_leaseToken, now);
            context.BackupFileStateChanges.Add(new BackupFileStateChange(
                Guid.NewGuid(),
                file.Id,
                file.TaskId,
                BackupFileStatus.DeletePending,
                BackupFileStatus.Deleted,
                PlanLocationRetentionWork.DeletedReason,
                now));
            context.AuditRecords.Add(new(null, "backup.file.plan_retention.commit", "BackupFile", file.Id.ToString("N"), "succeeded", PlanLocationRetentionWork.DeletedReason));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
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
