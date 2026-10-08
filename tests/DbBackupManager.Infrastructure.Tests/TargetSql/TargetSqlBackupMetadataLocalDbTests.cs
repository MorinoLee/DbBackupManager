using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlBackupMetadataLocalDbTests(ITestOutputHelper output)
{
    private readonly List<Exception> proofFailures = [];

    [Fact]
    public async Task OrdinaryFullIsTheActualDifferentialBase()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var full = await scope.BackupAsync("managed", BackupRunPurpose.PlanFull);
        await scope.ChangeAsync();
        var diff = await scope.BackupAsync("first", BackupRunPurpose.PlanDifferential);

        AssertRelationship(full, diff);
        AssertActive(full, await scope.ReadActiveAsync());
        AssertDependency(full, diff, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
        CompleteProof();
    }

    [Fact]
    public async Task CopyOnlyFullDoesNotReplaceTheOrdinaryFullBase()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var full = await scope.BackupAsync("managed", BackupRunPurpose.PlanFull);
        await scope.ChangeAsync();
        var copy = await scope.BackupAsync("temporary", BackupRunPurpose.AdHocCopyOnlyFull);
        AssertMetadata(copy.Header, BackupType.Full, copyOnly: true);
        AssertMetadata(copy.History, BackupType.Full, copyOnly: true);
        Assert.Equal(full.Header.Database, copy.Header.Database);
        Assert.Equal(full.Header.Branch, copy.Header.Branch);
        Assert.NotEqual(full.Header.BackupSetGuid, copy.Header.BackupSetGuid);
        AssertActive(full, await scope.ReadActiveAsync());
        await scope.ChangeAsync();
        var diff = await scope.BackupAsync("after_copy", BackupRunPurpose.PlanDifferential);

        AssertRelationship(full, diff);
        Assert.NotEqual(copy.Header.BackupSetGuid, diff.Header.DifferentialBaseGuid);
        AssertActive(full, await scope.ReadActiveAsync());
        AssertDependency(full, diff, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
        CompleteProof();
    }

    [Fact]
    public async Task MultipleDifferentialsAllDependOnFullRatherThanPreviousDifferential()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var full = await scope.BackupAsync("managed", BackupRunPurpose.PlanFull);
        var observedGuids = new HashSet<Guid>();
        for (var index = 0; index < 3; index++)
        {
            await scope.ChangeAsync();
            var diff = await scope.BackupAsync($"diff_{index}", BackupRunPurpose.PlanDifferential);
            AssertRelationship(full, diff);
            Assert.True(observedGuids.Add(diff.Header.BackupSetGuid!.Value));
            Assert.DoesNotContain(diff.Header.DifferentialBaseGuid!.Value, observedGuids);
            AssertActive(full, await scope.ReadActiveAsync());
            AssertDependency(full, diff, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
        }

        CompleteProof();
    }

    [Fact]
    public async Task ExternalOrdinaryFullReplacesTheBaseBeforeDifferential()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var managed = await scope.BackupAsync("managed", BackupRunPurpose.PlanFull);
        await scope.ChangeAsync();
        var external = await scope.BackupAsync("external", BackupRunPurpose.PlanFull, external: true);
        await scope.ChangeAsync();
        var diff = await scope.BackupAsync("after_external", BackupRunPurpose.PlanDifferential);

        AssertRelationship(external, diff);
        Assert.NotEqual(managed.Header.BackupSetGuid, diff.Header.DifferentialBaseGuid);
        Assert.NotEqual(managed.Header.CheckpointLsn, diff.Header.DifferentialBaseLsn);
        var active = await scope.ReadActiveAsync();
        AssertActive(external, active);
        AssertDependency(managed, diff, DifferentialBaselineConclusion.Unmanaged, DifferentialBaselineReason.ExternalFullObserved, external);
        AssertActiveDecision(managed, active, DifferentialBaselineConclusion.Unmanaged, DifferentialBaselineReason.ExternalFullObserved, external);
        CompleteProof();
    }

    [Fact]
    public async Task ExternalFullAfterDifferentialChangesActiveBaseButNotHistoricalDependency()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var managed = await scope.BackupAsync("managed", BackupRunPurpose.PlanFull);
        await scope.ChangeAsync();
        var diff = await scope.BackupAsync("historical", BackupRunPurpose.PlanDifferential);
        AssertRelationship(managed, diff);
        AssertActive(managed, await scope.ReadActiveAsync());
        await scope.ChangeAsync();
        var external = await scope.BackupAsync("later_external", BackupRunPurpose.PlanFull, external: true);
        var reread = await scope.ReadBackupAsync(diff.Label, diff.Path);

        Assert.Equal(diff.Header, reread.Header);
        Assert.Equal(diff.History, reread.History);
        Assert.NotEqual(diff.Adapted.Active.Baseline.DataFileBases[0].BaseBackupSetGuid,
            reread.Adapted.Active.Baseline.DataFileBases[0].BaseBackupSetGuid);
        AssertRelationship(managed, reread);
        AssertDependency(managed, reread, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified, external);
        var active = await scope.ReadActiveAsync();
        AssertActive(external, active);
        AssertActiveDecision(managed, active, DifferentialBaselineConclusion.Unmanaged, DifferentialBaselineReason.ExternalFullObserved, external);
        CompleteProof();
    }

    [Fact]
    public async Task UncommittedTransactionSpanningFullAndDifferentialSatisfiesEachLsnRule()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        await using var writer = scope.NewConnection();
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync();
        try
        {
            await LocalDbBackupMetadataProofScope.ExecuteAsync(writer,
                "UPDATE dbo.SyntheticPrimary SET [Value] = 1000 WHERE Id = 1; INSERT INTO dbo.SyntheticPrimary VALUES (1000, 1000);", transaction);
            await AssertUncommittedAsync(writer, transaction);
            var full = await scope.BackupAsync("long_transaction", BackupRunPurpose.PlanFull);
            // 活动事务的日志必须进入 FULL；空库样本不能证明这个条件。
            Assert.NotEqual(full.Header.FirstLsn, full.Header.CheckpointLsn);
            await AssertUncommittedAsync(writer, transaction);
            await scope.ChangeAsync(); // 修改另一个数据文件，避免等待未提交行的锁。
            var diff = await scope.BackupAsync("long_transaction", BackupRunPurpose.PlanDifferential);
            await AssertUncommittedAsync(writer, transaction);

            AssertRelationship(full, diff);
            AssertActive(full, await scope.ReadActiveAsync());
            AssertDependency(full, diff, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        await using var verifyRollback = new SqlCommand("SELECT COUNT(*) FROM dbo.SyntheticPrimary WHERE Id = 1000;", writer);
        Assert.Equal(0, (int)(await verifyRollback.ExecuteScalarAsync())!);
        CompleteProof();
    }

    private static async Task AssertUncommittedAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand("""
            SELECT @@TRANCOUNT,
                (SELECT COUNT(*) FROM sys.dm_tran_session_transactions WHERE session_id = @@SPID AND is_user_transaction = 1),
                (SELECT COUNT(*) FROM dbo.SyntheticPrimary WHERE Id = 1000);
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    private void AssertRelationship(ProofBackup full, ProofBackup diff)
    {
        Check(() => AssertSourceRelationship(full.Header, diff.Header));
        Check(() => AssertSourceRelationship(full.History, diff.History));
        foreach (var pair in new[] { (full.Adapted.Header.Metadata, diff.Adapted.Header.Metadata),
            (full.Adapted.History.Metadata, diff.Adapted.History.Metadata) })
            Check(() => Assert.Multiple(
                () => Assert.Equal(pair.Item1.BackupSetGuid.Value, pair.Item2.DifferentialBaseGuid.Value),
                () => Assert.Equal(pair.Item1.CheckpointLsn.Value, pair.Item2.DifferentialBaseLsn.Value),
                () => Assert.Equal(pair.Item2.DifferentialBaseLsn.Value, pair.Item2.DatabaseBackupLsn.Value)));
    }

    private static void AssertSourceRelationship(ProofMetadata full, ProofMetadata diff)
    {
        AssertMetadata(full, BackupType.Full);
        AssertMetadata(diff, BackupType.Differential);
        Assert.NotNull(diff.DifferentialBaseGuid);
        Assert.NotNull(diff.DifferentialBaseLsn);
        Assert.NotNull(diff.DatabaseBackupLsn);
        // 三条独立断言，保留每个字段的期望值和实际值；真实反例不能改成组合布尔值或跳过。
        Assert.Multiple(
            () => Assert.Equal(full.BackupSetGuid, diff.DifferentialBaseGuid),
            () => Assert.Equal(full.CheckpointLsn, diff.DifferentialBaseLsn),
            () => Assert.Equal(diff.DifferentialBaseLsn, diff.DatabaseBackupLsn),
            () => Assert.Equal(full.Database, diff.Database),
            () => Assert.Equal(full.Branch, diff.Branch));
    }

    private static void AssertMetadata(ProofMetadata metadata, BackupType type, bool copyOnly = false)
    {
        Assert.Equal(type, metadata.Type);
        Assert.Equal(copyOnly, metadata.IsCopyOnly);
        Assert.True(metadata.HasChecksums);
        Assert.NotNull(metadata.BackupSetGuid);
        Assert.NotNull(metadata.Database.DatabaseGuid);
        Assert.NotNull(metadata.Database.FamilyGuid);
        Assert.NotNull(metadata.Branch.FirstRecoveryForkId);
        Assert.NotNull(metadata.Branch.RecoveryForkId);
        Assert.Equal(metadata.Branch.FirstRecoveryForkId, metadata.Branch.RecoveryForkId);
        Assert.Null(metadata.ForkPointLsn);
        Assert.NotNull(metadata.FirstLsn);
        Assert.NotNull(metadata.LastLsn);
        Assert.NotNull(metadata.CheckpointLsn);
        Assert.NotNull(metadata.DatabaseBackupLsn);
        Assert.NotNull(metadata.StartedLocal);
        Assert.NotNull(metadata.FinishedLocal);
        Assert.True(metadata.FirstLsn.Value.Value < metadata.LastLsn.Value.Value);
        Assert.True(metadata.StartedLocal <= metadata.FinishedLocal);
        if (type == BackupType.Full)
        {
            Assert.Null(metadata.DifferentialBaseGuid);
            Assert.Null(metadata.DifferentialBaseLsn);
        }
    }

    private void AssertActive(ProofBackup full, ProofActiveBaseline active)
    {
        Check(() => Assert.Multiple(
            () => Assert.Equal(full.Header.Database, active.Database),
            () => Assert.NotNull(active.CurrentRecoveryForkId),
            () => Assert.Equal(full.Header.Branch.FirstRecoveryForkId, active.CurrentRecoveryForkId),
            () => Assert.Equal(full.Header.Branch.RecoveryForkId, active.CurrentRecoveryForkId)));
        Check(() => Assert.All(active.Files, file =>
        {
            Assert.NotNull(file.BaseTimeRaw);
            Assert.Multiple(
                () => Assert.Equal(full.Header.BackupSetGuid, file.Evidence.BaseBackupSetGuid),
                () => Assert.Equal(full.Header.CheckpointLsn, file.Evidence.BaseLsn));
        }));
        AssertActiveDecision(full, active, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
    }

    private void AssertDependency(ProofBackup managed, ProofBackup diff, DifferentialBaselineConclusion conclusion, string reason, ProofBackup? external = null)
    {
        foreach (var metadata in new[] { diff.Header, diff.History })
        {
            var decision = DifferentialBaselineRules.EvaluateDependency(
                managed.Header.Database, metadata.DifferentialEvidence, [Candidate(managed)], external?.Header.FullEvidence);
            output.WriteLine("历史依赖：{0}", decision);
            Check(() => Assert.Multiple(
                () => Assert.Equal(conclusion, decision.Conclusion),
                () => Assert.Equal(reason, decision.ReasonCode),
                () => Assert.Equal(conclusion == DifferentialBaselineConclusion.Verified ? managed.Header.BackupSetGuid : null, decision.ManagedFullId)));
        }
    }

    private void AssertActiveDecision(ProofBackup managed, ProofActiveBaseline active, DifferentialBaselineConclusion conclusion, string reason, ProofBackup? external = null)
    {
        var decision = DifferentialBaselineRules.EvaluateActiveBaseline(
            managed.Header.Database, active.Evidence, [Candidate(managed)], external?.Header.FullEvidence);
        output.WriteLine("活动基线：{0}", decision);
        Check(() => Assert.Multiple(
            () => Assert.Equal(conclusion, decision.Conclusion),
            () => Assert.Equal(reason, decision.ReasonCode),
            () => Assert.Equal(conclusion == DifferentialBaselineConclusion.Verified ? managed.Header.BackupSetGuid : null, decision.ManagedFullId)));
    }

    // 收集断言失败只为完成所有独立读数；场景结束仍抛出原始失败，不降级或跳过。
    private void Check(Action assertion)
    {
        var failure = Record.Exception(assertion);
        if (failure is not null)
        {
            output.WriteLine("实证断言失败：{0}", failure);
            proofFailures.Add(failure);
        }
    }

    private void CompleteProof()
    {
        if (proofFailures.Count != 0)
        {
            throw new AggregateException("真实元数据未满足全部预期，须维护者裁定。", proofFailures);
        }
    }

    private static ManagedFullBackupCandidate Candidate(ProofBackup full) => new(full.Header.BackupSetGuid!.Value, full.Header.FullEvidence);
}
