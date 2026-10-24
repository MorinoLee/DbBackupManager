using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlLogReadinessSessionTests
{
    private static readonly Guid DatabaseGuid = Guid.NewGuid();
    private static readonly Guid FamilyGuid = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task B08DeterministicCompleteChangeKeepsBothObservationsAndMarksUnstable()
    {
        var query = new Query(
            [State(At), State(At.AddSeconds(1))],
            [Recovery(At.AddSeconds(2), Guid.NewGuid()), Recovery(At.AddSeconds(3), Guid.NewGuid())]);
        await using var session = Session(query);

        var evidence = await session.ReadLogReadinessAsync(Request(), CancellationToken.None);

        Assert.False(evidence.StableAcrossRead);
        Assert.NotNull(evidence.RecoveryRecheck);
        Assert.NotEqual(evidence.Recovery.CurrentRecoveryForkId, evidence.RecoveryRecheck!.CurrentRecoveryForkId);
        Assert.Contains(new("recovery_status", BackupMetadataReadProblem.SourceConflict), evidence.Recovery.Issues);
        Assert.Contains(new("state", BackupMetadataReadProblem.SourceConflict), evidence.Database.Issues);
        Assert.True(evidence.Recovery.QueryComplete);
        Assert.True(evidence.RecoveryRecheck.QueryComplete);
    }

    [Fact]
    public async Task B08RecoveryRecheckFailureIsUnknownAndPreservesRecheckEvidence()
    {
        var failed = Recovery(At.AddSeconds(3), Guid.NewGuid()) with
        {
            QueryComplete = false,
            FailureCode = TargetSqlFailureCode.AuthorizationDenied,
            Issues = [new("source", BackupMetadataReadProblem.ReadFailed)],
        };
        var query = new Query([State(At), State(At.AddSeconds(1))], [Recovery(At.AddSeconds(2), Guid.NewGuid()), failed]);
        await using var session = Session(query);

        var evidence = await session.ReadLogReadinessAsync(Request(), CancellationToken.None);

        Assert.Null(evidence.StableAcrossRead);
        Assert.NotNull(evidence.RecoveryRecheck);
        Assert.Equal(TargetSqlFailureCode.AuthorizationDenied, evidence.RecoveryRecheck!.FailureCode);
        Assert.Equal(At.AddSeconds(3), evidence.RecoveryRecheck.ObservedAtUtc);
        Assert.Contains(new("source", BackupMetadataReadProblem.ReadFailed), evidence.RecoveryRecheck.Issues);
        Assert.Null(evidence.Recovery.FailureCode);
        Assert.DoesNotContain(evidence.Recovery.Issues,
            x => x.Problem == BackupMetadataReadProblem.SourceConflict);
        Assert.DoesNotContain(evidence.Database.Issues,
            x => x.Problem == BackupMetadataReadProblem.SourceConflict);
    }

    [Fact]
    public async Task B08IncompleteInitialRecoveryCannotBecomeStableFromDatabaseRecheck()
    {
        var incomplete = Recovery(At.AddSeconds(2), Guid.NewGuid()) with
        {
            QueryComplete = false,
            FailureCode = TargetSqlFailureCode.TimedOut,
            Issues = [new("source", BackupMetadataReadProblem.ReadFailed)],
        };
        var query = new Query([State(At), State(At.AddSeconds(1))], [incomplete]);
        await using var session = Session(query);

        var evidence = await session.ReadLogReadinessAsync(Request(), CancellationToken.None);

        Assert.Null(evidence.StableAcrossRead);
        Assert.Null(evidence.RecoveryRecheck);
        Assert.Equal(TargetSqlFailureCode.TimedOut, evidence.Recovery.FailureCode);
        Assert.Equal(1, query.RecoveryCalls);
    }

    [Fact]
    public async Task B08RecheckReadFailureIsUnknownNotConflict()
    {
        var recheckFailed = State(At.AddSeconds(1)) with
        {
            QueryComplete = false,
            FailureCode = TargetSqlFailureCode.AuthorizationDenied,
            Issues = [new("source", BackupMetadataReadProblem.ReadFailed)],
        };
        var query = new Query([State(At), recheckFailed], [Recovery(At.AddSeconds(2), Guid.NewGuid())]);
        await using var session = Session(query);

        var evidence = await session.ReadLogReadinessAsync(Request(), CancellationToken.None);

        Assert.Null(evidence.StableAcrossRead);
        Assert.NotNull(evidence.Recheck);
        Assert.Equal(TargetSqlFailureCode.AuthorizationDenied, evidence.Recheck!.FailureCode);
        Assert.DoesNotContain(evidence.Database.Issues,
            x => x.Problem == BackupMetadataReadProblem.SourceConflict);
        Assert.Equal(1, query.RecoveryCalls);
    }

    [Fact]
    public async Task B08CompleteUnchangedObservationsAreStableAndRetained()
    {
        var fork = Guid.NewGuid();
        var query = new Query([State(At), State(At.AddSeconds(1))],
            [Recovery(At.AddSeconds(2), fork), Recovery(At.AddSeconds(3), fork)]);
        await using var session = Session(query);

        var evidence = await session.ReadLogReadinessAsync(Request(), CancellationToken.None);

        Assert.True(evidence.StableAcrossRead);
        Assert.NotNull(evidence.RecoveryRecheck);
        Assert.Equal(fork, evidence.RecoveryRecheck!.CurrentRecoveryForkId);
        Assert.DoesNotContain(evidence.Recovery.Issues,
            x => x.Problem == BackupMetadataReadProblem.SourceConflict);
        Assert.DoesNotContain(evidence.Database.Issues,
            x => x.Problem == BackupMetadataReadProblem.SourceConflict);
    }

    private static SqlClientTargetSqlSession Session(Query query) =>
        new(new SqlConnection(), logReadinessQuery: query);

    private static TargetSqlLogReadinessRequest Request() =>
        new(Guid.NewGuid(), "synthetic", new(DatabaseGuid, FamilyGuid), 30);

    private static TargetSqlDatabaseReadinessObservation State(DateTimeOffset at) =>
        new(3, "synthetic", TargetSqlDatabaseState.Online, TargetSqlRecoveryModel.Full, true, at, []);

    private static TargetSqlRecoveryStatusObservation Recovery(DateTimeOffset at, Guid fork) =>
        new(true, new(DatabaseGuid, FamilyGuid), fork, fork, TargetSqlLogLsnState.ObservedNull,
            null, true, at, []);

    private sealed class Query(
        IReadOnlyList<TargetSqlDatabaseReadinessObservation> states,
        IReadOnlyList<TargetSqlRecoveryStatusObservation> recoveries) : ITargetSqlLogReadinessQuery
    {
        private int _stateIndex;
        private int _recoveryIndex;
        public int RecoveryCalls => _recoveryIndex;

        public Task<TargetSqlDatabaseReadinessObservation> ReadDatabaseStateAsync(
            TargetSqlLogReadinessRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(states[_stateIndex++]);

        public Task<TargetSqlRecoveryStatusObservation> ReadRecoveryStatusAsync(
            TargetSqlLogReadinessRequest request,
            int? databaseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(recoveries[_recoveryIndex++]);
    }
}
