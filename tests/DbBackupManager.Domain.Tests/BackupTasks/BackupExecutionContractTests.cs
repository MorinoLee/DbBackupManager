using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Domain.Tests.BackupTasks;

public sealed class BackupExecutionContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IdentityCanOnlyBeBoundBeforeInvocationAndNeverOverwritten()
    {
        var attempt = Attempt();
        var database = Guid.NewGuid();
        var family = Guid.NewGuid();
        attempt.BindSqlIdentity(database, family);
        attempt.BindSqlIdentity(database, family);
        Assert.Throws<InvalidOperationException>(() => attempt.BindSqlIdentity(Guid.NewGuid(), family));
        attempt.AdmitDifferential(Guid.NewGuid(), Guid.NewGuid(), Now);
        attempt.MarkBackupRunning(Now);
        Assert.Throws<InvalidOperationException>(() => AttemptStarted().BindSqlIdentity(database, family));
        Assert.Equal(database, attempt.ExpectedDatabaseGuid);
    }

    [Theory]
    [InlineData(BackupExecutionOperationKind.Admission)]
    [InlineData(BackupExecutionOperationKind.Metadata)]
    [InlineData(BackupExecutionOperationKind.VerifyLocal)]
    [InlineData(BackupExecutionOperationKind.Recovery)]
    public void OnlyUnfrozenReadObservationsCanBeAbandoned(BackupExecutionOperationKind kind)
    {
        var operation = Operation(kind);
        operation.Abandon();
        Assert.Equal(BackupExecutionOperationState.Abandoned, operation.State);
        Assert.Throws<InvalidOperationException>(() => operation.Freeze(new()));
    }

    [Theory]
    [InlineData(BackupExecutionOperationKind.SqlResult)]
    [InlineData(BackupExecutionOperationKind.Transfer)]
    [InlineData(BackupExecutionOperationKind.ValidateCopy)]
    public void UncertainWritesCannotBeAbandonedAndIssuedAgain(BackupExecutionOperationKind kind) =>
        Assert.Throws<InvalidOperationException>(() => Operation(kind).Abandon());

    [Fact]
    public void FrozenPayloadRetainsLsnContentAndRejectsConflictingReplay()
    {
        var facts = new BackupExecutionFacts
        {
            Metadata = new() { FirstLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012345m)) },
            Content = new() { State = BackupContentState.Verified, DigestAlgorithm = "SHA256", DigestHex = new string('a', 64), LengthBytes = 4096 }
        };
        var operation = Operation(BackupExecutionOperationKind.Metadata);
        Assert.Throws<InvalidOperationException>(operation.MarkApplied);
        operation.Freeze(facts);
        operation.Freeze(facts with { });
        Assert.Throws<InvalidOperationException>(() => operation.Freeze(facts with { SqlSuccessObserved = true }));
        operation.MarkApplied();
        operation.MarkApplied();
        Assert.Equal(facts, operation.Facts);
        Assert.Throws<InvalidOperationException>(operation.Abandon);
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData("abcd", 100)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 100)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0)]
    public void VerifiedContentRequiresCanonicalFullDigestAndPositiveLength(string? digest, long length) =>
        Assert.Throws<ArgumentException>(() => new BackupContentEvidence
        { State = BackupContentState.Verified, DigestAlgorithm = "SHA256", DigestHex = digest, LengthBytes = length }.Validate());

    [Fact]
    public void SameLengthDoesNotProveSameContentAndUnknownNeverMatches()
    {
        var a = new BackupContentEvidence { State = BackupContentState.Verified, DigestAlgorithm = "SHA256", DigestHex = new string('a', 64), LengthBytes = 100 };
        Assert.False(a.MatchesContent(a with { DigestHex = new string('b', 64) }));
        Assert.False(new BackupContentEvidence().MatchesContent(new()));
        Assert.True(a.MatchesContent(a with { StableObjectId = null }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TerminationRequiresBothOriginalCallAndCallerEvidence(bool ended, bool cannotInvoke)
    {
        var authorization = Authorization();
        Assert.Throws<ArgumentException>(() => authorization.RecordTermination(Guid.NewGuid(), Guid.NewGuid(),
            BackupInvocationTerminationKind.PlatformCompleted, Now, ended, cannotInvoke));
        Assert.Null(authorization.TerminalObservedAtUtc);
    }

    [Fact]
    public void UnknownSessionCannotBeReleasedUsingRecoveredEvidence()
    {
        var authorization = Authorization();
        Assert.Throws<ArgumentException>(() => authorization.RecordTermination(Guid.NewGuid(), Guid.NewGuid(),
            BackupInvocationTerminationKind.RecoveredTerminated, Now, true, true));
        var mutation = Guid.NewGuid();
        var evidence = Guid.NewGuid();
        authorization.RecordTermination(mutation, evidence, BackupInvocationTerminationKind.PlatformConfirmedFailed, Now, true, true);
        authorization.RecordTermination(mutation, evidence, BackupInvocationTerminationKind.PlatformConfirmedFailed, Now, true, true);
        Assert.Throws<InvalidOperationException>(() => authorization.RecordTermination(mutation, Guid.NewGuid(),
            BackupInvocationTerminationKind.PlatformConfirmedFailed, Now, true, true));
    }

    [Fact]
    public void RecoveryObservationCannotPretendToBePlatformCompletion() =>
        Assert.Throws<ArgumentException>(() => new BackupExecutionFacts
        { SqlOutcomeSource = BackupSqlOutcomeSource.RecoveredEvidence, SqlSuccessObserved = true, PlatformCompletedAtUtc = Now }.Validate());

    private static BackupPlanExecutionOperation Operation(BackupExecutionOperationKind kind) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, 1, Guid.NewGuid(), Now);
    private static BackupAttempt Attempt()
    {
        var id = Guid.NewGuid();
        return new(id, Guid.NewGuid(), 1, Now, new($@"D:\Synthetic\{id:N}.bak", $@"\\synthetic\share\{id:N}.bak", null, null, null));
    }
    private static BackupAttempt AttemptStarted() { var value = Attempt(); value.MarkBackupRunning(Now); return value; }
    private static BackupInvocationAuthorization Authorization() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now,
            new() { CallerIncarnationId = Guid.NewGuid() });
}
