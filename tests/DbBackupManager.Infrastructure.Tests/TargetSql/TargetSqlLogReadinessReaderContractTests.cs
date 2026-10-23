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

    private sealed class FixedResolver : ITargetSqlCredentialResolver
    {
        public ValueTask<TargetSqlCredentialResolution> ResolveSqlPasswordAsync(
            Guid credentialReferenceId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(TargetSqlCredentialResolution.Succeeded(
                TargetSqlCredentialLease.CreateAndClear("synthetic-user", "synthetic-value".ToCharArray())));
    }

    private sealed class FixedFactory(SlowReadinessSession session) : ITargetSqlClientSessionFactory
    {
        public Task<ITargetSqlClientSession> OpenAsync(
            TargetSqlConnectionInput input,
            TargetSqlCredentialLease credential,
            CancellationToken cancellationToken) =>
            Task.FromResult<ITargetSqlClientSession>(session);
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
