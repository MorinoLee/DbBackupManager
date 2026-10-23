using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Infrastructure.TargetSql;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlLogReadinessReaderContractTests
{
    [Fact]
    public async Task B09TimeoutIsBoundedAndClassifiedWithoutGrantingReadiness()
    {
        var session = new SlowReadinessSession();
        var reader = new SqlClientTargetSqlLogReadinessReader(
            new FixedResolver(),
            new FixedFactory(session));
        var request = new TargetSqlLogReadinessRequest(
            Guid.NewGuid(),
            "synthetic",
            new BackupDatabaseIdentity(Guid.NewGuid(), Guid.NewGuid()),
            1);
        var started = DateTimeOffset.UtcNow;

        var evidence = await reader.ReadAsync(
            new TargetSqlConnectionInput("synthetic-sql", Guid.NewGuid(), true, false, null, 30),
            request);

        Assert.Equal(TargetSqlFailureCode.TimedOut, evidence.Database.FailureCode);
        Assert.Equal(TargetSqlFailurePhase.DatabaseReadiness, evidence.Database.FailurePhase);
        Assert.False(evidence.Database.QueryComplete);
        Assert.Equal(TargetSqlLogLsnState.Unknown, evidence.Recovery.LastLogBackupLsnState);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.True(session.Cancelled);
    }

    [Fact]
    public async Task B09InternalDeadlineThatReturnsPartialEvidenceIsTimeoutAndKeepsFacts()
    {
        var session = new PartialCancelledSession();
        var reader = new SqlClientTargetSqlLogReadinessReader(new FixedResolver(), new FixedFactory(session));
        var request = new TargetSqlLogReadinessRequest(Guid.NewGuid(), "synthetic",
            new BackupDatabaseIdentity(Guid.NewGuid(), Guid.NewGuid()), 1);

        var evidence = await reader.ReadAsync(
            new TargetSqlConnectionInput("synthetic-sql", Guid.NewGuid(), true, false, null, 30), request);

        Assert.Equal(TargetSqlFailureCode.TimedOut, evidence.Recovery.FailureCode);
        Assert.Null(evidence.Database.FailureCode);
        Assert.Equal(PartialCancelledSession.DatabaseAt, evidence.Database.ObservedAtUtc);
        Assert.Equal(PartialCancelledSession.RecoveryAt, evidence.Recovery.ObservedAtUtc);
        Assert.Contains(new("source", BackupMetadataReadProblem.ReadFailed), evidence.Recovery.Issues);
    }

    [Fact]
    public async Task B09ExternalCancellationThatReturnsPartialEvidenceStaysCancelled()
    {
        var session = new PartialCancelledSession();
        var reader = new SqlClientTargetSqlLogReadinessReader(new FixedResolver(), new FixedFactory(session));
        var request = new TargetSqlLogReadinessRequest(Guid.NewGuid(), "synthetic",
            new BackupDatabaseIdentity(Guid.NewGuid(), Guid.NewGuid()), 30);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        var evidence = await reader.ReadAsync(
            new TargetSqlConnectionInput("synthetic-sql", Guid.NewGuid(), true, false, null, 30), request, caller.Token);

        Assert.Equal(TargetSqlFailureCode.Cancelled, evidence.Recovery.FailureCode);
        Assert.Equal(PartialCancelledSession.RecoveryAt, evidence.Recovery.ObservedAtUtc);
    }

    private sealed class PartialCancelledSession : ITargetSqlClientSession, ITargetSqlLogReadinessSession
    {
        public static readonly DateTimeOffset DatabaseAt = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset RecoveryAt = DatabaseAt.AddSeconds(1);

        public Task<TargetSqlServerInfo> ReadServerInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TargetSqlServerInfo("17.0.4025.3", null, null, 4, 17, true));

        public Task<TargetSqlDatabaseCatalog> ReadDatabasesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TargetSqlBackupIdentity> InspectBackupIdentityAsync(
            TargetSqlBackupIdentityRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TargetSqlBackupVerification> VerifyBackupAsync(
            TargetSqlBackupVerificationRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<TargetSqlLogReadinessEvidence> ReadLogReadinessAsync(
            TargetSqlLogReadinessRequest request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 与真实 session 一致：捕获取消并返回局部证据，而不是向 reader 抛出。
            }

            return new(
                request.PlatformDatabaseId,
                request.DatabaseName,
                request.ExpectedDatabase,
                null,
                new(3, "synthetic", TargetSqlDatabaseState.Online, TargetSqlRecoveryModel.Full, true, DatabaseAt, []),
                new(true, request.ExpectedDatabase, Guid.NewGuid(), Guid.NewGuid(),
                    TargetSqlLogLsnState.Unknown, null, false, RecoveryAt,
                    [new("source", BackupMetadataReadProblem.ReadFailed)],
                    TargetSqlFailureCode.Cancelled, TargetSqlFailurePhase.RecoveryStatusRead),
                null,
                true,
                null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedResolver : ITargetSqlCredentialResolver
    {
        public ValueTask<TargetSqlCredentialResolution> ResolveSqlPasswordAsync(
            Guid credentialReferenceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(TargetSqlCredentialResolution.Succeeded(
                TargetSqlCredentialLease.CreateAndClear("synthetic-user", "synthetic-value".ToCharArray())));
    }

    private sealed class FixedFactory(ITargetSqlClientSession session) : ITargetSqlClientSessionFactory
    {
        public Task<ITargetSqlClientSession> OpenAsync(
            TargetSqlConnectionInput input,
            TargetSqlCredentialLease credential,
            CancellationToken cancellationToken) =>
            Task.FromResult(session);
    }

    private sealed class SlowReadinessSession : ITargetSqlClientSession, ITargetSqlLogReadinessSession
    {
        public bool Cancelled { get; private set; }

        public Task<TargetSqlServerInfo> ReadServerInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TargetSqlServerInfo("17.0.4025.3", null, null, 4, 17, true));

        public Task<TargetSqlDatabaseCatalog> ReadDatabasesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TargetSqlBackupIdentity> InspectBackupIdentityAsync(
            TargetSqlBackupIdentityRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TargetSqlBackupVerification> VerifyBackupAsync(
            TargetSqlBackupVerificationRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<TargetSqlLogReadinessEvidence> ReadLogReadinessAsync(
            TargetSqlLogReadinessRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            throw new InvalidOperationException("不可达。");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
