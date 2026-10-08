using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupMetadataReconciliationTests
{
    private static readonly Guid DatabaseGuid = Guid.NewGuid(), FamilyGuid = Guid.NewGuid(), Fork = Guid.NewGuid();
    private static readonly Guid PlatformDatabaseId = Guid.NewGuid(), CredentialId = Guid.NewGuid();
    private static readonly BackupDatabaseIdentity Identity = new(DatabaseGuid, FamilyGuid);

    [Fact]
    public async Task ManagedFullUsesActualGuidRatherThanLatestFullAndChecksBothSources()
    {
        var full = Full();
        var diff = Diff(full);
        var lookup = new Lookup(full);
        var reader = new Reader(Evidence(diff, full));
        var service = new BackupMetadataReconciliationService(reader, lookup, new Store());
        var prepared = await service.PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.ActiveBaseline.Conclusion);
        Assert.Equal(lookup.SetId, prepared.Command.BaseBackupSetId);
        Assert.All(lookup.Guids, guid => Assert.Equal(full.BackupSetGuid.Value, guid));
        Assert.Equal(2, prepared.Command.Observations.Count(x => x.Kind == BackupSetEvidenceKind.Backup));
        Assert.DoesNotContain(prepared.Command.Observations.Where(x => x.Kind == BackupSetEvidenceKind.ActiveBaseline),
            x => x.Metadata.FirstRecoveryForkId.State == BackupMetadataState.Known);
    }

    [Fact]
    public async Task LaterExternalFullChangesActiveBaselineWithoutOverwritingHistoricalDependency()
    {
        var full = Full();
        var external = Full();
        var diff = Diff(full);
        var evidence = Evidence(diff, full) with
        {
            Active = Active(external),
            ReferencedFulls = [Source(full), Source(external)]
        };
        var lookup = new Lookup(full);
        var prepared = await new BackupMetadataReconciliationService(new Reader(evidence), lookup, new Store()).PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(lookup.SetId, prepared.Command.BaseBackupSetId);
        Assert.Equal(DifferentialBaselineConclusion.Unmanaged, prepared.ActiveBaseline.Conclusion);
        Assert.Equal(DifferentialBaselineReason.ExternalFullObserved, prepared.ActiveBaseline.ReasonCode);
        Assert.Single(prepared.Command.Observations, x => x.Kind == BackupSetEvidenceKind.ActiveBaseline
            && x.Assessment == prepared.ActiveBaseline);
    }

    [Fact]
    public async Task ExternalBaseIsOnlyEvidenceAndNeverRegisteredAsAManagedFull()
    {
        var full = Full();
        var prepared = await new BackupMetadataReconciliationService(new Reader(Evidence(Diff(full), full)),
            new Lookup(), new Store()).PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Unmanaged, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineReason.ExternalFullObserved, prepared.HistoricalDependency.ReasonCode);
        Assert.Null(prepared.Command.BaseBackupSetId);
        Assert.Equal(BackupType.Differential, prepared.Command.Metadata.Type.Value);
        Assert.Single(prepared.Command.Observations, x => x.Kind == BackupSetEvidenceKind.Dependency);
    }

    [Fact]
    public async Task ThisAttemptsOrdinaryFullCanBeTheNewManagedActiveBaseline()
    {
        var full = Full();
        var prepared = await new BackupMetadataReconciliationService(new Reader(Evidence(full, full)), new Lookup(), new Store())
            .PrepareAsync(Request());
        Assert.Equal(BackupSetAssessment.NotApplicable, prepared.HistoricalDependency);
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.ActiveBaseline.Conclusion);
        Assert.Null(prepared.Command.BaseBackupSetId);
        Assert.Equal(full, prepared.Command.Metadata);
    }

    [Fact]
    public async Task MultipleDataFileBasesAreUnknownWithoutSelectingTheFirst()
    {
        var full = Full();
        var evidence = Evidence(Diff(full), full) with
        {
            Active = new(new(Identity, Fork,
                [new(full.BackupSetGuid.Value, full.CheckpointLsn.Value), new(Guid.NewGuid(), new BackupLsn(999))]), null, [])
        };
        var prepared = await new BackupMetadataReconciliationService(new Reader(evidence), new Lookup(full), new Store()).PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Unknown, prepared.ActiveBaseline.Conclusion);
        Assert.Equal(DifferentialBaselineReason.MultipleBases, prepared.ActiveBaseline.ReasonCode);
        Assert.Equal(2, prepared.Command.Observations.Count(x => x.Kind == BackupSetEvidenceKind.ActiveBaseline));
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.HistoricalDependency.Conclusion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyCurrentForkRemainsUnknownAndCanBeRegistered(bool empty)
    {
        var full = Full();
        var evidence = Evidence(Diff(full), full) with
        {
            Active = new(new(Identity, empty ? Guid.Empty : null,
                [new(full.BackupSetGuid.Value, full.CheckpointLsn.Value)]), null, [])
        };
        var prepared = await new BackupMetadataReconciliationService(new Reader(evidence), new Lookup(full), new Store()).PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Unknown, prepared.ActiveBaseline.Conclusion);
        Assert.Equal(DifferentialBaselineReason.MissingFields, prepared.ActiveBaseline.ReasonCode);
        var observation = Assert.Single(prepared.Command.Observations, x => x.Kind == BackupSetEvidenceKind.ActiveBaseline);
        observation.Metadata.Validate();
        Assert.Equal(BackupMetadataState.Unknown, observation.Metadata.RecoveryForkId.State);
    }

    [Fact]
    public async Task ContradictoryConfirmedLsnRetainsBothSourcesAndMarksMismatch()
    {
        var full = Full();
        var diff = Diff(full);
        var history = diff with { DatabaseBackupLsn = BackupMetadata.Known(new BackupLsn(123)) };
        var evidence = Evidence(diff, full) with { History = Source(history) };
        var prepared = await new BackupMetadataReconciliationService(new Reader(evidence), new Lookup(full), new Store()).PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineReason.SourceConflict, prepared.HistoricalDependency.ReasonCode);
        Assert.Null(prepared.Command.BaseBackupSetId);
        Assert.Contains(prepared.Command.Observations, x => x.Metadata == diff);
        Assert.Contains(prepared.Command.Observations, x => x.Metadata == history);
    }

    [Theory]
    [InlineData(BaselineEvidenceStatus.PermissionDenied, "baseline.permission_denied")]
    [InlineData(BaselineEvidenceStatus.HistoryNotFound, "baseline.history_not_found")]
    [InlineData(BaselineEvidenceStatus.MissingFields, "baseline.missing_fields")]
    public async Task SqlSucceededButMetadataFailedRemainsTwoSeparateFacts(BaselineEvidenceStatus status, string reason)
    {
        var failed = Source(new()) with { Status = status };
        var evidence = new TargetSqlBackupMetadataEvidence(failed with { Source = BackupSetEvidenceSource.BackupHeader },
            failed, new(new(new(null, null), null, [], status), null, []), [], null, null);
        var store = new Store();
        var service = new BackupMetadataReconciliationService(new Reader(evidence), new Lookup(), store);
        var request = Request();
        var prepared = await service.PrepareAsync(request);
        Assert.True(prepared.Command.SqlSuccessObserved);
        Assert.Equal(request.PlatformCompletedAtUtc, prepared.Command.Completion.CompletedAtUtc);
        Assert.Equal(DifferentialBaselineConclusion.Unknown, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(reason, prepared.HistoricalDependency.ReasonCode);
        await service.CommitAsync(prepared);
        Assert.Same(prepared.Command, store.Command);
    }

    [Fact]
    public async Task LostCommitResponseReplaysIdenticalMutationWithoutReadingSqlAgain()
    {
        var full = Full();
        var reader = new Reader(Evidence(Diff(full), full));
        var store = new Store { LoseFirstResponse = true };
        var service = new BackupMetadataReconciliationService(reader, new Lookup(full), store);
        var prepared = await service.PrepareAsync(Request());
        await Assert.ThrowsAsync<IOException>(() => service.CommitAsync(prepared));
        var result = await service.CommitAsync(prepared);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, result.Code);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(2, store.Calls);
        Assert.Same(prepared.Command, store.Command);
    }

    [Fact]
    public async Task MissingReliableTimeZoneDoesNotInventUtcDuringRecoveryRegistration()
    {
        var full = Full() with { SqlFinishedLocal = BackupMetadata.Known(new DateTime(2026, 10, 8, 1, 0, 0)) };
        var prepared = await new BackupMetadataReconciliationService(new Reader(Evidence(full, full) with
        { CurrentServerOffset = TimeSpan.Zero }), new Lookup(full), new Store())
            .PrepareAsync(Request() with { PlatformCompletedAtUtc = null });
        Assert.Null(prepared.Command.Completion.CompletedAtUtc);
        Assert.Equal(BackupCompletionTimeReason.ServerTimeZoneUnknown, prepared.Command.Completion.ReasonCode);
        Assert.Equal(DateTimeKind.Unspecified, prepared.Command.Metadata.SqlFinishedLocal.Value!.Value.Kind);
        Assert.Equal(BackupSetAssessment.NotApplicable, prepared.HistoricalDependency);
    }

    [Fact]
    public async Task WrongAttemptIsRejectedBeforeTargetIo()
    {
        var reader = new Reader(Evidence(Full(), Full()));
        var request = Request();
        await Assert.ThrowsAsync<ArgumentException>(() => new BackupMetadataReconciliationService(reader, new Lookup(), new Store())
            .PrepareAsync(request with { File = new(Guid.NewGuid(), "synthetic", @"D:\Synthetic\attempt.bak", 60) }));
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task SameNameFullFromAnotherDatabaseIsMismatch()
    {
        var full = Full();
        var prepared = await new BackupMetadataReconciliationService(new Reader(Evidence(full, full)), new Lookup(), new Store())
            .PrepareAsync(Request() with { ExpectedDatabase = new(Guid.NewGuid(), FamilyGuid) });
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineReason.DatabaseIdentityMismatch, prepared.HistoricalDependency.ReasonCode);
    }

    [Fact]
    public async Task MissingOtherFieldsCannotHideConfirmedCopyOnlyConflict()
    {
        var full = Full();
        var diff = Diff(full) with { IsCopyOnly = BackupMetadata.Known(true), DatabaseBackupLsn = default };
        var evidence = Evidence(diff, full);
        evidence = evidence with
        {
            Header = evidence.Header with { Status = BaselineEvidenceStatus.MissingFields },
            History = evidence.History with { Status = BaselineEvidenceStatus.MissingFields }
        };
        var prepared = await new BackupMetadataReconciliationService(new Reader(evidence), new Lookup(full), new Store()).PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineReason.CopyOnlyDifferential, prepared.HistoricalDependency.ReasonCode);
    }

    [Fact]
    public async Task PartialCopyOnlyFullEvidenceCannotBeHiddenByManagedFull()
    {
        var full = Full();
        var evidence = Evidence(Diff(full), full) with
        {
            ReferencedFulls = [Source(full with { IsCopyOnly = BackupMetadata.Known(true), CheckpointLsn = default })
                with { Status = BaselineEvidenceStatus.MissingFields }]
        };
        var prepared = await new BackupMetadataReconciliationService(new Reader(evidence), new Lookup(full), new Store()).PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineReason.CopyOnlyFull, prepared.HistoricalDependency.ReasonCode);
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, prepared.ActiveBaseline.Conclusion);
    }

    [Fact]
    public async Task WrongAttemptFileOrConnectionIsRejectedBeforeTargetIo()
    {
        var reader = new Reader(Evidence(Full(), Full()));
        var service = new BackupMetadataReconciliationService(reader, new Lookup(), new Store());
        var request = Request();
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(request with
        { File = new(request.Lease.BackupAttemptId, "synthetic", @"D:\Synthetic\wrong.bak", 60) }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(request with
        { Connection = new("synthetic-other-sql", CredentialId, true, false, null, 30) }));
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(BaselineEvidenceStatus.HistoryNotFound, BackupMetadataReadProblem.NotFound)]
    [InlineData(BaselineEvidenceStatus.MissingFields, BackupMetadataReadProblem.NotUnique)]
    public async Task MissingOrAmbiguousReferencedFullCannotBeHiddenByManagedRecord(
        BaselineEvidenceStatus status, BackupMetadataReadProblem problem)
    {
        var full = Full();
        var evidence = Evidence(Diff(full), full) with
        {
            ReferencedFulls = [new(BackupSetEvidenceSource.Msdb, new(), status, [new("record", problem)],
                RequestedBackupSetGuid: full.BackupSetGuid.Value,
                AmbiguousRecords: problem == BackupMetadataReadProblem.NotUnique ? [full, full] : [])]
        };
        var prepared = await new BackupMetadataReconciliationService(new Reader(evidence), new Lookup(full), new Store()).PrepareAsync(Request());
        Assert.Equal(DifferentialBaselineConclusion.Unknown, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineConclusion.Unknown, prepared.ActiveBaseline.Conclusion);
        Assert.Null(prepared.Command.BaseBackupSetId);
        Assert.Equal(problem == BackupMetadataReadProblem.NotUnique ? 3 : 1,
            prepared.Command.Observations.Count(x => x.Kind == BackupSetEvidenceKind.Dependency));
    }

    private static BackupSetMetadata Full() => new()
    {
        BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
        DatabaseGuid = BackupMetadata.Known(DatabaseGuid),
        FamilyGuid = BackupMetadata.Known(FamilyGuid),
        Type = BackupMetadata.Known(BackupType.Full),
        IsCopyOnly = BackupMetadata.Known(false),
        CheckpointLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012345m)),
        FirstRecoveryForkId = BackupMetadata.Known(Fork),
        RecoveryForkId = BackupMetadata.Known(Fork),
        DifferentialBaseGuid = BackupMetadata.NotApplicable<Guid>(),
        DifferentialBaseLsn = BackupMetadata.NotApplicable<BackupLsn>()
    };
    private static BackupSetMetadata Diff(BackupSetMetadata full) => full with
    {
        BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
        Type = BackupMetadata.Known(BackupType.Differential),
        DifferentialBaseGuid = full.BackupSetGuid,
        DifferentialBaseLsn = full.CheckpointLsn,
        DatabaseBackupLsn = full.CheckpointLsn
    };
    private static BackupMetadataSourceEvidence Source(BackupSetMetadata metadata) => new(BackupSetEvidenceSource.Msdb, metadata, BaselineEvidenceStatus.Complete, []);
    private static BackupActiveMetadataEvidence Active(BackupSetMetadata full) => new(new(Identity, Fork,
        [new(full.BackupSetGuid.Value, full.CheckpointLsn.Value)]), null, []);
    private static TargetSqlBackupMetadataEvidence Evidence(BackupSetMetadata metadata, BackupSetMetadata full) => new(
        Source(metadata) with { Source = BackupSetEvidenceSource.BackupHeader }, Source(metadata), Active(full), [Source(full)], null, null);
    private static BackupMetadataReconciliationRequest Request()
    {
        var now = new DateTimeOffset(2026, 10, 8, 2, 0, 0, TimeSpan.Zero);
        var lease = new LeaseHandle(Guid.NewGuid(), Guid.NewGuid(), BackupLeasePurpose.Execution, BackupTaskStage.VerifyLocal,
            Guid.NewGuid(), now.AddMinutes(10), [1]);
        return new(lease, Guid.NewGuid(), PlatformDatabaseId, Guid.NewGuid(), now, Identity,
            new("synthetic-sql", CredentialId, true, false, null, 30),
            new(lease.BackupAttemptId, "synthetic", @"D:\Synthetic\attempt.bak", 60), true, now.AddSeconds(-1));
    }
    private sealed class Reader(TargetSqlBackupMetadataEvidence evidence) : ITargetSqlBackupMetadataReader
    {
        public int Calls { get; private set; }
        public Task<TargetSqlBackupMetadataEvidence> ReadAsync(TargetSqlConnectionInput connection, TargetSqlBackupMetadataRequest request,
            CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(evidence); }
    }
    private sealed class Lookup(params BackupSetMetadata[] fulls) : IBackupMetadataReconciliationLookup
    {
        public Task<BackupMetadataAttemptContext?> ReadAttemptAsync(Guid taskId, Guid attemptId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BackupMetadataAttemptContext?>(new(PlatformDatabaseId, "synthetic", @"D:\Synthetic\attempt.bak",
                new("synthetic-sql", CredentialId, true, false, null, 30)));
        public Task<BackupMetadataRegistrationReplay?> ReadReplayAsync(LeaseHandle lease, Guid databaseId, Guid backupSetId,
            Guid mutationId, CancellationToken cancellationToken = default) => Task.FromResult<BackupMetadataRegistrationReplay?>(null);
        public Guid SetId { get; } = Guid.NewGuid();
        public List<Guid> Guids { get; } = [];
        public Task<IReadOnlyList<ManagedFullBackupCandidate>> FindAsync(Guid databaseId, Guid backupSetGuid, CancellationToken cancellationToken = default)
        {
            Guids.Add(backupSetGuid);
            return Task.FromResult<IReadOnlyList<ManagedFullBackupCandidate>>(fulls.Where(x => x.BackupSetGuid.Value == backupSetGuid)
                .Select(x => new ManagedFullBackupCandidate(SetId, BackupMetadataReconciliationService.Full(x, BaselineEvidenceStatus.Complete))).ToArray());
        }
    }
    private sealed class Store : IBackupSetRegistrationStore
    {
        public RegisterBackupSetCommand? Command { get; private set; }
        public int Calls { get; private set; }
        public bool LoseFirstResponse { get; init; }
        public Task<BackupTaskStoreResult<BackupSetRegistrationResult>> RegisterAsync(RegisterBackupSetCommand command, CancellationToken cancellationToken = default)
        {
            if (Command is not null) Assert.Same(Command, command);
            Command = command;
            Calls++;
            if (Calls == 1 && LoseFirstResponse) throw new IOException("合成提交响应丢失");
            return Task.FromResult(new BackupTaskStoreResult<BackupSetRegistrationResult>(Calls == 1
                ? BackupTaskStoreResultCode.Succeeded : BackupTaskStoreResultCode.AlreadyApplied));
        }
        public Task<BackupTaskStoreResult<BackupFile>> RegisterCopyAsync(LeaseHandle lease, BackupFile file, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("核对用例不能创建副本。");
    }
}
