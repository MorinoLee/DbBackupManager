using System.Data;
using System.Text.Json;
using DbBackupManager.Application.BackupLogs;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class LogAssessmentCommitStore(IDbContextFactory<PlatformDbContext> factory,
    TimeProvider clock) : ILogAssessmentCommitStore
{
    public async Task<LogAssessmentReadResult> ReadAsync(Guid databaseId, Guid mutationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.LogAssessmentCommits.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DatabaseId == databaseId && x.MutationId == mutationId, cancellationToken);
        return row is null ? new(LogAssessmentReadCode.NotFound) : Reconstruct(row);
    }

    public Task<LogAssessmentCommitResult> CommitAsync(PreparedLogAssessment prepared,
        CancellationToken cancellationToken = default) =>
        CommitCoreAsync(prepared, 1, cancellationToken);

    private async Task<LogAssessmentCommitResult> CommitCoreAsync(PreparedLogAssessment prepared,
        int attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!string.Equals(prepared.RequestFingerprint,
                LogAssessmentCanonicalizer.RequestFingerprint(prepared.Input), StringComparison.Ordinal)
            || !string.Equals(prepared.PayloadFingerprint,
                LogAssessmentCanonicalizer.PayloadFingerprint(prepared), StringComparison.Ordinal)
            || prepared.PayloadFingerprint.Length != 64
            || prepared.Observations.Select(x => x.EntryNumber).Distinct().Count() != prepared.Observations.Count)
            return new(LogAssessmentCommitCode.Conflict);
        try
        {
            await using var strategy = await factory.CreateDbContextAsync(cancellationToken);
            return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted, cancellationToken);
                var existing = await db.LogAssessmentCommits.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.DatabaseId == prepared.Input.DatabaseId
                        && x.MutationId == prepared.Input.MutationId, cancellationToken);
                if (existing is not null)
                    return Compare(existing, prepared);

                var managedIds = prepared.ResolvedCandidates.Where(x => x.IsManaged)
                    .Select(x => x.BackupSetId).Distinct().ToArray();
                if (managedIds.Length > 0)
                {
                    var owners = await db.BackupSets.AsNoTracking()
                        .Where(x => managedIds.Contains(x.Id))
                        .Select(x => new { x.Id, x.DatabaseId })
                        .ToArrayAsync(cancellationToken);
                    if (owners.Length != managedIds.Length
                        || owners.Any(x => x.DatabaseId != prepared.Input.DatabaseId))
                        return new(LogAssessmentCommitCode.Conflict);
                }

                var committedAt = clock.GetUtcNow();
                db.LogAssessmentCommits.Add(ToEntity(prepared, committedAt));
                foreach (var observation in prepared.Observations.OrderBy(x => x.EntryNumber))
                {
                    db.LogObservationEntries.Add(new()
                    {
                        Id = LogAssessmentCanonicalizer.DeterministicId("observation",
                            prepared.AssessmentId, observation.EntryNumber),
                        AssessmentId = prepared.AssessmentId,
                        EntryNumber = observation.EntryNumber,
                        Role = observation.Role,
                        Source = observation.Source.ToString(),
                        Status = observation.Status.ToString(),
                        ObservedAtUtc = observation.ObservedAtUtc,
                        HasRequiredFields = observation.HasRequiredFields,
                        FailureCode = observation.FailureCode,
                        FailurePhase = observation.FailurePhase,
                    });
                }

                var coverage = prepared.Coverage;
                db.LogCoverageEntries.Add(new()
                {
                    Id = LogAssessmentCanonicalizer.DeterministicId("coverage", prepared.AssessmentId),
                    AssessmentId = prepared.AssessmentId,
                    ScopeKind = coverage.ScopeKind,
                    StorageTargetId = coverage.StorageTargetId,
                    Conclusion = coverage.Conclusion,
                    FrontierLsn = coverage.FrontierLsn?.Value,
                    RestorePointLsn = prepared.DataStart.RestorePointLsn?.Value,
                });

                foreach (var generation in prepared.Generations.Where(x => x.GenerationId is not null))
                {
                    var generationId = generation.GenerationId!.Value;
                    var fact = await db.LogGenerationFacts.FindAsync([generationId], cancellationToken);
                    if (fact is null)
                    {
                        db.LogGenerationFacts.Add(new()
                        {
                            Id = generationId,
                            DatabaseId = prepared.Input.DatabaseId,
                            CreatedAtUtc = committedAt,
                        });
                    }
                    else if (fact.DatabaseId != prepared.Input.DatabaseId)
                    {
                        return new(LogAssessmentCommitCode.Conflict);
                    }

                    var existingGeneration = await db.LogGenerationEvidence
                        .SingleOrDefaultAsync(x => x.EvidenceKey == generation.EvidenceKey, cancellationToken);
                    if (existingGeneration is not null)
                    {
                        if (existingGeneration.GenerationId != generationId) return new(LogAssessmentCommitCode.Conflict);
                        continue;
                    }

                    db.LogGenerationEvidence.Add(new()
                    {
                        Id = LogAssessmentCanonicalizer.DeterministicId("generation-evidence", generation.EvidenceKey),
                        GenerationId = generationId,
                        EvidenceKey = generation.EvidenceKey,
                        EvidenceKind = generation.EvidenceKind.ToString(),
                        DatabaseId = prepared.Input.DatabaseId,
                        SourceDatabaseGuid = generation.SourceDatabaseGuid,
                        FamilyGuid = generation.FamilyGuid,
                        FirstRecoveryForkId = generation.FirstRecoveryForkId,
                        RecoveryForkId = generation.RecoveryForkId,
                        ObservedAtUtc = generation.ObservedAtUtc,
                        RecordedAtUtc = committedAt,
                        Status = generation.Status.ToString(),
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(LogAssessmentCommitCode.Succeeded, prepared);
            });
        }
        catch (DbUpdateException)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var existing = await db.LogAssessmentCommits.AsNoTracking()
                .SingleOrDefaultAsync(x => x.DatabaseId == prepared.Input.DatabaseId
                    && x.MutationId == prepared.Input.MutationId, cancellationToken);
            if (existing is not null) return Compare(existing, prepared);
            if (attempt < 2)
            {
                await Task.Delay(50, cancellationToken);
                return await CommitCoreAsync(prepared, attempt + 1, cancellationToken);
            }

            return new(LogAssessmentCommitCode.Conflict);
        }
    }

    private static LogAssessmentCommitResult Compare(LogAssessmentCommit existing,
        PreparedLogAssessment prepared)
    {
        if (existing.RequestFingerprint != prepared.RequestFingerprint
            || existing.PayloadFingerprint != prepared.PayloadFingerprint)
            return new(LogAssessmentCommitCode.Conflict);
        try
        {
            var saved = LogAssessmentCanonicalizer.Deserialize(existing.PayloadJson);
            return saved.PayloadFingerprint == prepared.PayloadFingerprint
                ? new(LogAssessmentCommitCode.AlreadyApplied, saved)
                : new(LogAssessmentCommitCode.Conflict);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return new(LogAssessmentCommitCode.Conflict);
        }
    }

    private static LogAssessmentReadResult Reconstruct(LogAssessmentCommit row)
    {
        try
        {
            var prepared = LogAssessmentCanonicalizer.Deserialize(row.PayloadJson);
            return prepared.RequestFingerprint == row.RequestFingerprint
                && prepared.PayloadFingerprint == row.PayloadFingerprint
                && prepared.PayloadFingerprint == LogAssessmentCanonicalizer.PayloadFingerprint(prepared)
                && prepared.RequestFingerprint == LogAssessmentCanonicalizer.RequestFingerprint(prepared.Input)
                    ? new(LogAssessmentReadCode.Found, prepared)
                    : new(LogAssessmentReadCode.Conflict);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return new(LogAssessmentReadCode.Conflict);
        }
    }

    private static LogAssessmentCommit ToEntity(PreparedLogAssessment prepared, DateTimeOffset committedAt) => new()
    {
        Id = prepared.AssessmentId,
        DatabaseId = prepared.Input.DatabaseId,
        MutationId = prepared.Input.MutationId,
        SchemaVersion = prepared.SchemaVersion,
        RequestFingerprint = prepared.RequestFingerprint,
        PayloadFingerprint = prepared.PayloadFingerprint,
        PayloadJson = LogAssessmentCanonicalizer.Serialize(prepared),
        ScopeKind = prepared.Input.ScopeKind,
        StorageTargetId = prepared.Input.StorageTargetId,
        PlanId = prepared.Input.PlanId,
        PlanVersionId = prepared.Input.PlanVersionId,
        ObservedAtUtc = prepared.Input.ObservedAtUtc,
        PreparedAtUtc = prepared.PreparedAtUtc,
        CommittedAtUtc = committedAt,
        TechnicalReadiness = prepared.Technical.Readiness,
        TechnicalReasonCode = prepared.Technical.ReasonCode,
        DataStartAvailability = prepared.DataStart.Availability,
        DataStartReasonCode = prepared.DataStart.ReasonCode,
        CoverageConclusion = prepared.Coverage.Conclusion,
        PriorCallMayEnterAdmission = prepared.PriorCall.MayEnterAdmission,
        PriorCallReasonCode = prepared.PriorCall.ReasonCode,
        DatabaseBudgetAllows = prepared.DatabaseBudget.AllowsNewLog,
        DatabaseBudgetReasonCode = prepared.DatabaseBudget.ReasonCode,
        GlobalBudgetAllows = prepared.GlobalBudget.AllowsNewLog,
        GlobalBudgetReasonCode = prepared.GlobalBudget.ReasonCode,
    };
}
