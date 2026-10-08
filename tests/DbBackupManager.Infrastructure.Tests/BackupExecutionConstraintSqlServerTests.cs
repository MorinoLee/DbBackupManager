using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupExecutionConstraintSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ClearBackupTaskDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public static IEnumerable<object[]> NullableFacts()
    {
        string[] fields = ["BackupSetGuid", "DatabaseGuid", "FamilyGuid", "Type", "FirstLsn", "LastLsn", "CheckpointLsn",
            "DatabaseBackupLsn", "DifferentialBaseLsn", "DifferentialBaseGuid", "FirstRecoveryForkId", "RecoveryForkId", "ForkPointLsn",
            "IsCopyOnly", "HasBackupChecksums", "IsCompressed", "IsDamaged", "IsSnapshot", "HasIncompleteMetadata", "SqlStartedLocal", "SqlFinishedLocal"];
        foreach (var table in new[] { "BackupPlanExecutionOperations", "BackupPlanExecutionObservations" })
        {
            foreach (var field in fields)
                yield return [table, field, $"[{field}State]='Known',[{field}]=NULL", 547];
            yield return [table, "DifferentialFields", "[TypeState]='Unknown',[Type]=NULL,[DifferentialBaseLsnState]='NotApplicable',[DifferentialBaseLsn]=NULL,[DifferentialBaseGuidState]='NotApplicable',[DifferentialBaseGuid]=NULL", 547];
            yield return [table, "Assessment", "[AssessmentState]='Known',[Conclusion]=NULL,[BaselineReasonCode]='baseline.managed_full_verified'", 547];
            yield return [table, "Assessment", "[AssessmentState]='Known',[Conclusion]='Verified',[BaselineReasonCode]=NULL", 547];
            yield return [table, "Completion", "[CompletionSource]='PlatformObserved',[CompletedAtUtc]=NULL,[CompletionReasonCode]='completion.platform_observed'", 547];
            yield return [table, "ActiveAssessment", "[ActiveAssessmentState]='Known',[ActiveConclusion]=NULL,[ActiveReasonCode]='baseline.managed_full_verified'", 547];
            yield return [table, "ActiveAssessment", "[ActiveAssessmentState]='Known',[ActiveConclusion]='Verified',[ActiveReasonCode]=NULL", 547];
            yield return [table, "Content", "[ContentState]='Verified',[ContentDigestAlgorithm]='SHA256',[ContentDigest]=NULL,[ContentLengthBytes]=8192", 547];
            yield return [table, "Content", "[ContentState]='Verified',[ContentDigestAlgorithm]='SHA256',[ContentDigest]=0x0000000000000000000000000000000000000000000000000000000000000000,[ContentLengthBytes]=NULL", 547];
            yield return [table, "Content", "[ContentState]='Verified',[ContentDigestAlgorithm]=NULL,[ContentDigest]=0x0000000000000000000000000000000000000000000000000000000000000000,[ContentLengthBytes]=8192", 547];
            yield return [table, "Outcome", "[Outcome]=NULL", 515];
            yield return [table, "PlatformCompletedAtUtc", "[PlatformCompletedAtUtc]='2026-10-08T00:00:00+08:00',[SqlOutcomeSource]='PlatformResponse',[SqlSuccessObserved]=1", 547];
            yield return [table, "EvidenceAtUtc", "[EvidenceAtUtc]='2026-10-08T00:00:00+08:00'", 547];
        }
        yield return ["BackupPlanExecutionOperations", "Identity", "[Sequence]=NULL", 515];
        yield return ["BackupPlanExecutionOperations", "State", "[State]=NULL", 515];
        yield return ["BackupPlanExecutionObservations", "Source", "[Source]=NULL", 515];
    }

    [Theory]
    [MemberData(nameof(NullableFacts))]
    public async Task NullableReceiptFieldsCannotPassTheirChecks(string table, string constraint, string assignments, int errorNumber)
    {
        var work = await BackupSetTestData.WorkAsync(database);
        await using var db = database.CreateContext();
        var operation = new BackupPlanExecutionOperation(Guid.NewGuid(), work.Task.TaskId, work.Attempt.Id,
            BackupExecutionOperationKind.Metadata, 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
        // 默认未知字段为空是合法对照；逐字段改成 Known + NULL 必须被 SQL 拒绝。
        db.AddRange(operation, new BackupPlanExecutionObservation(Guid.NewGuid(), operation.Id, 0,
            BackupExecutionObservationSource.Comparison, BackupSetEvidenceKind.Backup, new()));
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (constraint == "Type")
        {
            // Type 的 NULL 也违反组合类型约束；临时隔离后证明单列约束独立拒绝，事务释放恢复原检查。
            var isolate = $"ALTER TABLE [{table}] NOCHECK CONSTRAINT [CK_{table}_DifferentialFields]";
            await db.Database.ExecuteSqlRawAsync(isolate);
        }
        var sql = $"UPDATE [{table}] SET {assignments}"; // 仅来自本测试固定列/赋值清单。
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(errorNumber, error.Number);
        if (errorNumber == 547) Assert.Contains($"CK_{table}_{constraint}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Binding", "[BindingState]='Known',[SessionId]=NULL,[SessionEstablishedLocal]='2026-10-08T00:00:00'", 547)]
    [InlineData("Binding", "[BindingState]='Known',[SessionId]=50,[SessionEstablishedLocal]=NULL", 547)]
    [InlineData("Termination", "[TerminalObservedAtUtc]=NULL,[TerminationKind]='PlatformCompleted',[TerminationEvidenceId]=NEWID(),[TerminationMutationId]=NEWID()", 547)]
    [InlineData("Termination", "[TerminalObservedAtUtc]='2026-10-08T00:00:00+00:00',[TerminationKind]=NULL,[TerminationEvidenceId]=NEWID(),[TerminationMutationId]=NEWID()", 547)]
    [InlineData("Termination", "[TerminalObservedAtUtc]='2026-10-08T00:00:00+00:00',[TerminationKind]='PlatformCompleted',[TerminationEvidenceId]=NULL,[TerminationMutationId]=NEWID()", 547)]
    [InlineData("Termination", "[TerminalObservedAtUtc]='2026-10-08T00:00:00+00:00',[TerminationKind]='PlatformCompleted',[TerminationEvidenceId]=NEWID(),[TerminationMutationId]=NULL", 547)]
    [InlineData("GrantedAtUtc", "[GrantedAtUtc]=NULL", 515)]
    public async Task AuthorizationNullableChecksRejectPartialFacts(string constraint, string assignments, int errorNumber)
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var store = new BackupInvocationAuthorizationStore(new BackupExecutionContractSqlServerTests.Factory(database), TimeProvider.System);
        await store.AuthorizeAsync(work.Lease, await ExecutionContractTestData.AuthorizationAsync(database, work));
        await using var db = database.CreateContext();
        var sql = $"UPDATE [BackupInvocationAuthorizations] SET {assignments}";
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(errorNumber, error.Number);
        if (errorNumber == 547) Assert.Contains($"CK_BackupInvocationAuthorizations_{constraint}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SqlIdentity", "[ExpectedDatabaseGuid]=NEWID(),[ExpectedFamilyGuid]=NULL")]
    [InlineData("SqlIdentity", "[ExpectedDatabaseGuid]=NULL,[ExpectedFamilyGuid]=NEWID()")]
    [InlineData("DifferentialAdmission", "[AdmittedFullBackupSetId]=NULL,[AdmissionRecoveryForkId]=NEWID(),[AdmissionObservedAtUtc]='2026-10-08T00:00:00+00:00'")]
    [InlineData("DifferentialAdmission", "[AdmittedFullBackupSetId]=NULL,[AdmissionRecoveryForkId]=NULL,[AdmissionObservedAtUtc]='2026-10-08T00:00:00+00:00'")]
    [InlineData("DifferentialAdmission", "[AdmittedFullBackupSetId]=NULL,[AdmissionRecoveryForkId]=NEWID(),[AdmissionObservedAtUtc]=NULL")]
    public async Task AttemptBindingRequiresCompleteExplicitEvidence(string constraint, string assignments)
    {
        await BackupSetTestData.WorkAsync(database);
        await using var db = database.CreateContext();
        var sql = $"UPDATE [BackupAttempts] SET {assignments}";
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(547, error.Number);
        Assert.Contains($"CK_BackupAttempts_{constraint}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompositeOwnershipAndUniqueActiveDatabaseAreEnforcedBySql()
    {
        var first = await BackupSetTestData.WorkAsync(database);
        await using var db = database.CreateContext();
        var policy = (await db.BackupTasks.SingleAsync(x => x.Id == first.Task.TaskId)).PolicyId!.Value;
        var second = await BackupSetTestData.WorkAsync(database, policy);
        var op = new BackupPlanExecutionOperation(Guid.NewGuid(), first.Task.TaskId, first.Attempt.Id,
            BackupExecutionOperationKind.SqlResult, 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var other = new BackupPlanExecutionOperation(Guid.NewGuid(), second.Task.TaskId, second.Attempt.Id,
            BackupExecutionOperationKind.SqlResult, 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.AddRange(op, other);
        await db.SaveChangesAsync();
        var cross = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [BackupPlanExecutionOperations] SET [AttemptId]={second.Attempt.Id} WHERE [Id]={op.Id}"));
        Assert.Equal(547, cross.Number);
        Assert.Contains("FK_", cross.Message, StringComparison.Ordinal);
        var authorization = new BackupInvocationAuthorization(Guid.NewGuid(), first.Snapshot.Identity.DatabaseId,
            first.Task.TaskId, first.Attempt.Id, Guid.NewGuid(), op.Id, DateTimeOffset.UtcNow, new() { CallerIncarnationId = Guid.NewGuid() });
        db.Add(authorization);
        await db.SaveChangesAsync();
        cross = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [BackupInvocationAuthorizations] SET [SqlOperationId]={other.Id} WHERE [Id]={authorization.Id}"));
        Assert.Equal(547, cross.Number);
        db.Add(new BackupInvocationAuthorization(Guid.NewGuid(), first.Snapshot.Identity.DatabaseId,
            second.Task.TaskId, second.Attempt.Id, Guid.NewGuid(), other.Id, DateTimeOffset.UtcNow, new() { CallerIncarnationId = Guid.NewGuid() }));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<SqlException>(duplicate.InnerException);
        Assert.Contains("DatabaseId", duplicate.InnerException!.Message, StringComparison.Ordinal);
        Assert.All(db.Model.GetEntityTypes().Where(x => x.ClrType == typeof(BackupInvocationAuthorization)
            || x.ClrType == typeof(BackupPlanExecutionOperation) || x.ClrType == typeof(BackupPlanExecutionObservation)).SelectMany(x => x.GetForeignKeys()),
            fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
    }
}
