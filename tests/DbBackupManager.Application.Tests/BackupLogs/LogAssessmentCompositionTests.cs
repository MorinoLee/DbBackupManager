using DbBackupManager.Application.BackupLogs;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests.BackupLogs;

public sealed class LogAssessmentCompositionTests
{
    private static readonly Guid DatabaseId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();
    private static readonly Guid PlanVersionId = Guid.NewGuid();
    private static readonly Guid ConfigId = Guid.NewGuid();
    private static readonly Guid CredentialId = Guid.NewGuid();
    private static readonly Guid DatabaseGuid = Guid.NewGuid();
    private static readonly Guid FamilyGuid = Guid.NewGuid();
    private static readonly Guid Fork = Guid.NewGuid();
    private static readonly Guid GenerationId = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task C01C26ReadyTechnicalAndStartMissingCoverageAreIndependent()
    {
        var full = FullCandidate();
        var prepared = await PrepareAsync(Request(candidates: [full]));

        Assert.Equal("Ready", prepared.Technical.Readiness);
        Assert.Equal("Available", prepared.DataStart.Availability);
        Assert.Equal("Gap", prepared.Coverage.Conclusion);
        Assert.True(prepared.BudgetAllows);
        Assert.False(prepared.PriorCall.ReusesOriginalInvocation);
    }

    [Fact]
    public async Task C02ObservedNullIsChainNotReadyNotUnknown()
    {
        var evidence = ReadyEvidence() with
        {
            Recovery = ReadyEvidence().Recovery with
            {
                LastLogBackupLsnState = TargetSqlLogLsnState.ObservedNull,
                LastLogBackupLsn = null,
            },
        };
        var prepared = await PrepareAsync(Request(candidates: [FullCandidate()]), evidence);

        Assert.Equal("NotReady", prepared.Technical.Readiness);
        Assert.Equal(LogReason.ChainNotReady, prepared.Technical.ReasonCode);
        Assert.Equal(TargetSqlLogLsnState.ObservedNull, prepared.Readiness.Recovery.LastLogBackupLsnState);
    }

    [Fact]
    public async Task C03PermissionFailureIsNotObservedNull()
    {
        var evidence = ReadyEvidence() with
        {
            Database = ReadyEvidence().Database with
            {
                FailureCode = TargetSqlFailureCode.AuthorizationDenied,
                FailurePhase = TargetSqlFailurePhase.DatabaseReadiness,
                QueryComplete = false,
            },
        };
        var prepared = await PrepareAsync(Request(candidates: [FullCandidate()]), evidence);

        Assert.Equal("Unknown", prepared.Technical.Readiness);
        Assert.Equal(LogReason.ObservationPermissionDenied, prepared.Technical.ReasonCode);
        Assert.NotEqual("NotReady", prepared.Technical.Readiness);
    }

    [Fact]
    public async Task C04OfflineIsNotReadyWithoutRecoveryAttempt()
    {
        var evidence = ReadyEvidence() with
        {
            Database = ReadyEvidence().Database with { State = TargetSqlDatabaseState.Offline },
            Recovery = ReadyEvidence().Recovery with
            {
                WasAttempted = false,
                QueryComplete = false,
                LastLogBackupLsnState = TargetSqlLogLsnState.Unknown,
            },
            StableAcrossRead = null,
        };
        var prepared = await PrepareAsync(Request(candidates: [FullCandidate()]), evidence);

        Assert.Equal("NotReady", prepared.Technical.Readiness);
        Assert.Equal(LogReason.DatabaseOffline, prepared.Technical.ReasonCode);
        Assert.False(prepared.Readiness.Recovery.WasAttempted);
    }

    [Fact]
    public async Task C05IdentityOrStabilityConflictIsUnknown()
    {
        var evidence = ReadyEvidence() with { ExpectedIdentityMatches = false, StableAcrossRead = false };
        var prepared = await PrepareAsync(Request(candidates: [FullCandidate()]), evidence);

        Assert.Equal("Unknown", prepared.Technical.Readiness);
        Assert.Equal(LogReason.ObservationConflict, prepared.Technical.ReasonCode);
    }

    [Fact]
    public async Task C06ActualLogAttributesRemainInPayloadAndDoNotCreateCoverage()
    {
        var actual = FullCandidate().Actual with
        {
            Type = BackupType.Log,
            RecoveryModel = DatabaseRecoveryModel.BulkLogged,
            HasBulkLoggedData = true,
            IsForceOffline = true,
        };
        var log = new LogAssessmentCandidate(Guid.NewGuid(), actual, LogLocationScope.Local,
            LogLocationCopyState.Available, true, null, null, null, Guid.NewGuid(),
            LogEvidenceStatus.Complete, true);
        var prepared = await PrepareAsync(Request(candidates: [FullCandidate(), log]));

        Assert.Contains(prepared.Candidates, x => x.BackupSetId == log.BackupSetId);
        Assert.NotEqual("Continuous", prepared.Coverage.Conclusion);
        Assert.Equal(LogObservationSource.Platform, log.Actual.Source);
        Assert.True(log.Actual.HasBulkLoggedData);
    }

    [Fact]
    public async Task C07MissingStartIsIndependentFromTechnicalReady()
    {
        var prepared = await PrepareAsync(Request(candidates: []));

        Assert.Equal("Ready", prepared.Technical.Readiness);
        Assert.Equal("Missing", prepared.DataStart.Availability);
        Assert.NotEqual(LogReason.StartManagedFull, prepared.DataStart.ReasonCode);
    }

    [Fact]
    public async Task C08UnorderedCoverageUsesDomainRules()
    {
        var full = FullCandidate();
        var log1 = LogCandidate(new BackupLsn(100), new BackupLsn(200));
        var log2 = LogCandidate(new BackupLsn(200), new BackupLsn(300));
        var prepared = await PrepareAsync(Request(candidates: [log2, full, log1]));

        Assert.Equal("Continuous", prepared.Coverage.Conclusion);
        Assert.Equal(new BackupLsn(300), prepared.Coverage.FrontierLsn);
        Assert.Equal(2, prepared.Coverage.UsedBackupSetIds.Count);
    }

    [Fact]
    public async Task C14BudgetMustPassBothScopesAndUnknownDoesNotReset()
    {
        var request = Request(candidates: [FullCandidate()]) with
        {
            DatabaseBudgetLimits = new(10, 1000, 100),
            DatabaseBudgetObservation = new(0, 0, 1000),
            GlobalBudgetLimits = new(10, 1000, 100),
            GlobalBudgetObservation = new(null, null, 1000),
        };
        var prepared = await PrepareAsync(request);

        Assert.True(prepared.DatabaseBudget.AllowsNewLog);
        Assert.False(prepared.GlobalBudget.AllowsNewLog);
        Assert.False(prepared.BudgetAllows);
        Assert.Equal(LogReason.BudgetObservationUnknown, prepared.GlobalBudget.ReasonCode);
    }

    [Fact]
    public async Task C16CommittedPreparedReplaysWithoutTargetRead()
    {
        var store = new FakeCommitStore();
        var reader = new FakeReader(ReadyEvidence());
        var service = new LogAssessmentCompositionService(reader, store);
        var request = Request(candidates: [FullCandidate()]);
        var prepared = await service.PrepareAsync(request);
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await service.CommitAsync(prepared)).Code);
        var calls = reader.Calls;

        var replay = await service.PrepareAsync(request);

        Assert.Equal(calls, reader.Calls);
        Assert.Equal(prepared.AssessmentId, replay.AssessmentId);
        Assert.Equal(prepared.PayloadFingerprint, replay.PayloadFingerprint);
    }

    [Fact]
    public async Task C16MissingPreparedReportsPayloadRequiredInsteadOfRecollecting()
    {
        var service = new LogAssessmentCompositionService(new FakeReader(ReadyEvidence()), new FakeCommitStore());
        var result = await service.ReadCommitAsync(DatabaseId, Guid.NewGuid());

        Assert.Equal(LogAssessmentReadCode.PreparedPayloadRequired, result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task R32o1ReaderResolvedCandidatesAreFrozenInPrepared()
    {
        var resolved = FullCandidate();
        var service = new LogAssessmentCompositionService(new FakeReader(ReadyEvidence()), new FakeCommitStore(),
            new FakeCandidateReader(resolved));
        var prepared = await service.PrepareAsync(Request(candidates: []));

        Assert.Empty(prepared.Input.Candidates);
        var candidate = Assert.Single(prepared.ResolvedCandidates);
        Assert.Equal(resolved.BackupSetId, candidate.BackupSetId);
        Assert.Equal(resolved.Actual.FirstLsn, candidate.Actual.FirstLsn);
        Assert.NotEmpty(prepared.CandidateSetRevision);
        var roundTrip = LogAssessmentCanonicalizer.Deserialize(LogAssessmentCanonicalizer.Serialize(prepared));
        Assert.Equal(candidate.Actual.FirstLsn, roundTrip.ResolvedCandidates.Single().Actual.FirstLsn);
        Assert.Equal(prepared.PayloadFingerprint, LogAssessmentCanonicalizer.PayloadFingerprint(roundTrip));
    }

    [Fact]
    public async Task R32o6CandidatePermutationDoesNotChangeRequestIdentity()
    {
        var store = new FakeCommitStore();
        var service = new LogAssessmentCompositionService(new FakeReader(ReadyEvidence()), store);
        var full = FullCandidate();
        var log = LogCandidate(new(100), new(200));
        var first = await service.PrepareAsync(Request(candidates: [full, log]));
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await service.CommitAsync(first)).Code);

        var replayRequest = Request(candidates: [log, full]) with { MutationId = first.Input.MutationId };
        var replay = await service.PrepareAsync(replayRequest);

        Assert.Equal(first.AssessmentId, replay.AssessmentId);
        Assert.Equal(first.PayloadFingerprint, replay.PayloadFingerprint);
    }

    [Fact]
    public async Task R32o4InnerConflictOrUnspecifiedEvidenceCannotProveGeneration()
    {
        var innerConflict = FullCandidate() with
        {
            Actual = FullCandidate().Actual with
            {
                Status = LogEvidenceStatus.SourceConflict,
                BeginsLogChain = true,
            },
        };
        var blocked = await PrepareAsync(Request(candidates: [innerConflict]));
        Assert.Empty(blocked.Generations);
        Assert.Null(blocked.Candidates[0].GenerationId);

        var unspecified = FullCandidate() with
        {
            Actual = FullCandidate().Actual with
            {
                Generation = new(Guid.NewGuid(), LogGenerationEvidenceKind.Unspecified),
            },
        };
        var unknown = await PrepareAsync(Request(candidates: [unspecified]));
        Assert.Empty(unknown.Generations);
        Assert.Null(unknown.Candidates[0].GenerationId);
    }

    [Fact]
    public async Task R32o6SameBackupMultipleSourceOrderIsCanonical()
    {
        var store = new FakeCommitStore();
        var service = new LogAssessmentCompositionService(new FakeReader(ReadyEvidence()), store);
        var first = FullCandidate();
        var header = first with
        {
            Actual = first.Actual with { Source = LogObservationSource.BackupHeader, ObservedAtUtc = At },
        };
        var msdb = first with
        {
            Actual = first.Actual with { Source = LogObservationSource.Msdb, ObservedAtUtc = At.AddSeconds(1) },
        };
        var prepared = await service.PrepareAsync(Request(candidates: [header, msdb]));
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await service.CommitAsync(prepared)).Code);

        var replay = await service.PrepareAsync(Request(candidates: [msdb, header]) with
        {
            MutationId = prepared.Input.MutationId,
        });

        Assert.Equal(prepared.PayloadFingerprint, replay.PayloadFingerprint);
    }

    [Fact]
    public async Task C21PriorCallStateRemainsIndependent()
    {
        var request = Request(candidates: [FullCandidate()]) with
        {
            PriorCall = new()
            {
                State = LogPriorCallState.Active,
                HasActiveAttempt = true,
            },
        };
        var prepared = await PrepareAsync(request);

        Assert.False(prepared.PriorCall.MayEnterAdmission);
        Assert.Equal(LogReason.PriorCallActive, prepared.PriorCall.ReasonCode);
        Assert.Equal("Ready", prepared.Technical.Readiness);
    }

    [Fact]
    public async Task R32o5DifferentSourceIdentityCannotBecomeRequestDataStart()
    {
        var candidate = FullCandidate();
        candidate = candidate with
        {
            Actual = candidate.Actual with { Database = new(Guid.NewGuid(), Guid.NewGuid()) },
        };

        var prepared = await PrepareAsync(Request(candidates: [candidate]));

        Assert.Equal("Missing", prepared.DataStart.Availability);
        Assert.Contains(prepared.Issues, x => x.Code == "log.candidate_identity_mismatch");
        Assert.Empty(prepared.Candidates);
    }

    [Fact]
    public async Task C22CandidateFromAnotherScopeIsNotMixed()
    {
        var remote = FullCandidate() with { Scope = LogLocationScope.Remote(Guid.NewGuid()) };
        var prepared = await PrepareAsync(Request(candidates: [remote]));

        Assert.Equal("Missing", prepared.DataStart.Availability);
        Assert.Contains(prepared.Issues, x => x.Code == "log.candidate_scope_mismatch");
        Assert.Empty(prepared.Candidates);
    }

    [Fact]
    public async Task C24OnlyPositiveGenerationEvidenceCreatesStableGeneration()
    {
        var actual = FullCandidate().Actual with { BeginsLogChain = true, Generation = null };
        var candidate = new LogAssessmentCandidate(Guid.NewGuid(), actual, LogLocationScope.Local,
            LogLocationCopyState.Available, true, new(true, false), null, null, Guid.NewGuid(),
            LogEvidenceStatus.Complete, true);
        var prepared = await PrepareAsync(Request(candidates: [candidate]));

        Assert.Single(prepared.Generations);
        Assert.NotNull(prepared.Generations[0].GenerationId);
        Assert.Equal(LogGenerationEvidenceKind.BeginsLogChain, prepared.Generations[0].EvidenceKind);
        Assert.Equal(prepared.Generations[0].GenerationId, prepared.Candidates[0].GenerationId);

        var conflicted = candidate with
        {
            Status = LogEvidenceStatus.SourceConflict,
            HasRequiredFields = false,
        };
        var blocked = await PrepareAsync(Request(candidates: [conflicted]));
        Assert.Empty(blocked.Generations);
        Assert.Null(blocked.Candidates[0].GenerationId);

        var full = await PrepareAsync(Request(candidates: [FullCandidate() with
        {
            Actual = FullCandidate().Actual with { Generation = null },
        }]));
        Assert.Empty(full.Generations);
    }

    [Fact]
    public async Task C25UnknownIdentityCanBePersistedAsFact()
    {
        var actual = FullCandidate().Actual with
        {
            Database = null,
            Branch = null,
            Generation = null,
            FirstLsn = null,
            LastLsn = null,
        };
        var candidate = new LogAssessmentCandidate(Guid.NewGuid(), actual, LogLocationScope.Local,
            LogLocationCopyState.Unknown, true, null, null, null, Guid.NewGuid(),
            LogEvidenceStatus.MissingFields, false);
        var store = new FakeCommitStore();
        var service = new LogAssessmentCompositionService(new FakeReader(ReadyEvidence()), store);
        var prepared = await service.PrepareAsync(Request(candidates: [candidate]));

        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await service.CommitAsync(prepared)).Code);
        Assert.Null(prepared.Candidates[0].GenerationId);
    }

    [Fact]
    public async Task C29BudgetReservationPlaceholderNeverSucceeds()
    {
        var result = await new UnavailableLogBudgetReservationStore().TryReserveAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, 10, false));

        Assert.Equal(LogBudgetReservationCode.StageNotOpen, result.Code);
        Assert.Null(result.ReservedBytes);
    }

    [Fact]
    public async Task C12StableReadFailureKeepsUnknownWithoutIdentityMismatch()
    {
        var evidence = ReadyEvidence() with { StableAcrossRead = null };
        var prepared = await PrepareAsync(Request(candidates: [FullCandidate()]), evidence);

        Assert.Equal("Unknown", prepared.Technical.Readiness);
        Assert.Equal(LogReason.ObservationMissing, prepared.Technical.ReasonCode);
    }

    [Fact]
    public async Task C15UnknownCapabilityDoesNotBecomeSupported()
    {
        var request = Request(candidates: [FullCandidate()]) with { Capability = LogCapabilityState.Unknown };
        var prepared = await PrepareAsync(request);

        Assert.Equal("Unknown", prepared.Technical.Readiness);
        Assert.Equal(LogReason.CapabilityUnknown, prepared.Technical.ReasonCode);
    }

    [Fact]
    public async Task C23MismatchedProtectionIsNotAcceptedAsContinuous()
    {
        var full = FullCandidate();
        var actual = full.Actual with { UseChecksum = false, UseCompression = true };
        var candidate = full with
        {
            Actual = actual,
            ProtectionExpectation = new(true, false),
        };
        var prepared = await PrepareAsync(Request(candidates: [candidate]));

        Assert.NotEqual("Continuous", prepared.Coverage.Conclusion);
        Assert.Contains(prepared.Coverage.RejectedBackupSetIds, id => id == candidate.BackupSetId);
    }

    [Fact]
    public async Task C11SourceConflictCandidateCannotProduceContinuousCoverage()
    {
        var full = FullCandidate();
        var conflicted = full with
        {
            Actual = full.Actual with { Status = LogEvidenceStatus.SourceConflict },
            Status = LogEvidenceStatus.SourceConflict,
            HasRequiredFields = false,
        };
        var log = LogCandidate(new BackupLsn(100), new BackupLsn(200));
        var prepared = await PrepareAsync(Request(candidates: [conflicted, log]));

        Assert.Equal("Unknown", prepared.DataStart.Availability);
        Assert.NotEqual("Continuous", prepared.Coverage.Conclusion);
    }

    private static async Task<PreparedLogAssessment> PrepareAsync(LogAssessmentRequest request,
        TargetSqlLogReadinessEvidence? evidence = null)
    {
        var service = new LogAssessmentCompositionService(
            new FakeReader(evidence ?? ReadyEvidence()), new FakeCommitStore());
        return await service.PrepareAsync(request);
    }

    private static LogAssessmentRequest Request(IReadOnlyList<LogAssessmentCandidate> candidates) => new(
        DatabaseId, Guid.NewGuid(), "synthetic", PlanId, PlanVersionId, ConfigId,
        new(DatabaseGuid, FamilyGuid), LogLocationScope.Local, LogRequestIntent.Manual, At, 60,
        new("synthetic-sql", CredentialId, true, false, null, 30), new(), LogCapabilityState.Supported,
        LogResourceAvailability.Available, new(10, 1000, 100), new(0, 0, 1000),
        new(10, 1000, 100), new(0, 0, 1000), true, null, candidates);

    private static LogAssessmentCandidate FullCandidate() => new(Guid.NewGuid(), new()
    {
        Type = BackupType.Full,
        IsCopyOnly = false,
        RecoveryModel = DatabaseRecoveryModel.Full,
        BeginsLogChain = false,
        HasBulkLoggedData = false,
        IsForceOffline = false,
        UseChecksum = true,
        UseCompression = false,
        IsDamaged = false,
        IsSnapshot = false,
        IsIncomplete = false,
        FirstLsn = new(10),
        LastLsn = new(100),
        DatabaseBackupLsn = new(0),
        ForkPointLsn = new(10),
        Database = new(DatabaseGuid, FamilyGuid),
        Branch = new(Fork, Fork),
        Generation = new(GenerationId, LogGenerationEvidenceKind.RecoveryFork),
        CompletedAtUtc = At.AddMinutes(-1),
        ObservedAtUtc = At.AddMinutes(-1),
        Source = LogObservationSource.Platform,
        Status = LogEvidenceStatus.Complete,
    }, LogLocationScope.Local, LogLocationCopyState.Available, true, new(true, false), null, null, Guid.NewGuid());

    private static LogAssessmentCandidate LogCandidate(BackupLsn first, BackupLsn last) => new(
        Guid.NewGuid(), new()
        {
            Type = BackupType.Log,
            IsCopyOnly = false,
            RecoveryModel = DatabaseRecoveryModel.Full,
            BeginsLogChain = false,
            HasBulkLoggedData = false,
            IsForceOffline = false,
            UseChecksum = true,
            UseCompression = false,
            IsDamaged = false,
            IsSnapshot = false,
            IsIncomplete = false,
            FirstLsn = first,
            LastLsn = last,
            DatabaseBackupLsn = new(0),
            ForkPointLsn = first,
            Database = new(DatabaseGuid, FamilyGuid),
            Branch = new(Fork, Fork),
            Generation = new(GenerationId, LogGenerationEvidenceKind.RecoveryFork),
            CompletedAtUtc = At.AddMinutes(-1),
            ObservedAtUtc = At.AddMinutes(-1),
            Source = LogObservationSource.BackupHeader,
            Status = LogEvidenceStatus.Complete,
        }, LogLocationScope.Local, LogLocationCopyState.Available, true, new(true, false), null, null, Guid.NewGuid());

    private static TargetSqlLogReadinessEvidence ReadyEvidence()
    {
        var database = new TargetSqlDatabaseReadinessObservation(1, "synthetic",
            TargetSqlDatabaseState.Online, TargetSqlRecoveryModel.Full, true, At, []);
        var recovery = new TargetSqlRecoveryStatusObservation(true, new(DatabaseGuid, FamilyGuid),
            Fork, Fork, TargetSqlLogLsnState.Known, new BackupLsn(50), true, At, []);
        return new(DatabaseId, "synthetic", new(DatabaseGuid, FamilyGuid),
            new TargetSqlServerInfo("17.0.4025.3", null, null, 4, 17, true),
            database, recovery, database, true, true, recovery);
    }

    private sealed class FakeReader(TargetSqlLogReadinessEvidence evidence) : ITargetSqlLogReadinessReader
    {
        public int Calls { get; private set; }
        public Task<TargetSqlLogReadinessEvidence> ReadAsync(TargetSqlConnectionInput connection,
            TargetSqlLogReadinessRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(evidence);
        }
    }

    private sealed class FakeCandidateReader(params LogAssessmentCandidate[] candidates)
        : ILogAssessmentCandidateReader
    {
        public Task<LogCandidateSnapshot> ReadAsync(LogAssessmentRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LogCandidateSnapshot(true, candidates, []));
    }

    private sealed class FakeCommitStore : ILogAssessmentCommitStore
    {
        private readonly Dictionary<(Guid, Guid), PreparedLogAssessment> _rows = [];
        public Task<LogAssessmentReadResult> ReadAsync(Guid databaseId, Guid mutationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.TryGetValue((databaseId, mutationId), out var value)
                ? new LogAssessmentReadResult(LogAssessmentReadCode.Found, value)
                : new LogAssessmentReadResult(LogAssessmentReadCode.NotFound));

        public Task<LogAssessmentCommitResult> CommitAsync(PreparedLogAssessment prepared,
            CancellationToken cancellationToken = default)
        {
            var key = (prepared.Input.DatabaseId, prepared.Input.MutationId);
            if (_rows.TryGetValue(key, out var existing))
                return Task.FromResult(new LogAssessmentCommitResult(
                    existing.RequestFingerprint == prepared.RequestFingerprint
                    && existing.PayloadFingerprint == prepared.PayloadFingerprint
                        ? LogAssessmentCommitCode.AlreadyApplied : LogAssessmentCommitCode.Conflict, existing));
            _rows[key] = prepared;
            return Task.FromResult(new LogAssessmentCommitResult(LogAssessmentCommitCode.Succeeded, prepared));
        }
    }
}
