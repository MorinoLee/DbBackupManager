using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupPlanExecutionOperationConfiguration : IEntityTypeConfiguration<BackupPlanExecutionOperation>
{
    public void Configure(EntityTypeBuilder<BackupPlanExecutionOperation> builder)
    {
        builder.ConfigureConcurrency();
        builder.ToTable("BackupPlanExecutionOperations", table =>
        {
            BackupExecutionContractMapping.CheckFacts(table, "BackupPlanExecutionOperations");
            table.HasCheckConstraint("CK_BackupPlanExecutionOperations_Identity",
                "[Sequence] >= 1 AND [IntendedBackupSetId] <> '00000000-0000-0000-0000-000000000000' AND DATEPART(TZOFFSET,[ObservedAtUtc]) = 0");
            table.HasCheckConstraint("CK_BackupPlanExecutionOperations_State",
                "[State] IN ('Reserved','Frozen','Applied','Abandoned') AND [Kind] IN ('Admission','SqlResult','Metadata','VerifyLocal','Transfer','ValidateCopy','Recovery') "
                + "AND ([State] <> 'Abandoned' OR [Kind] NOT IN ('SqlResult','Transfer','ValidateCopy'))");
        });
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        BackupExecutionContractMapping.MapFacts(builder.ComplexProperty(x => x.Facts));
        builder.HasIndex(x => new { x.TaskId, x.AttemptId, x.Kind, x.Sequence }).IsUnique();
        builder.HasAlternateKey(x => new { x.TaskId, x.AttemptId, x.Id });
        builder.HasOne<BackupAttempt>().WithMany().HasForeignKey(x => new { x.TaskId, x.AttemptId })
            .HasPrincipalKey(x => new { x.TaskId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupPlanExecutionObservationConfiguration : IEntityTypeConfiguration<BackupPlanExecutionObservation>
{
    public void Configure(EntityTypeBuilder<BackupPlanExecutionObservation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.ToTable("BackupPlanExecutionObservations", table =>
        {
            BackupExecutionContractMapping.CheckFacts(table, "BackupPlanExecutionObservations");
            table.HasCheckConstraint("CK_BackupPlanExecutionObservations_Source",
                "[EntryNumber] >= 0 AND [Source] IN ('Comparison','BackupHeader','Msdb','Database','Registration','SourceFile','RemotePartial','RemoteFinal','Termination') AND [Kind] IN ('Backup','Dependency','ActiveBaseline')");
        });
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        BackupExecutionContractMapping.MapFacts(builder.ComplexProperty(x => x.Facts));
        builder.HasIndex(x => new { x.OperationId, x.EntryNumber }).IsUnique();
        builder.HasOne<BackupPlanExecutionOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupInvocationAuthorizationConfiguration : IEntityTypeConfiguration<BackupInvocationAuthorization>
{
    public void Configure(EntityTypeBuilder<BackupInvocationAuthorization> builder)
    {
        builder.ConfigureConcurrency();
        builder.ToTable("BackupInvocationAuthorizations", table =>
        {
            table.HasCheckConstraint("CK_BackupInvocationAuthorizations_GrantedAtUtc", "DATEPART(TZOFFSET,[GrantedAtUtc]) = 0");
            table.HasCheckConstraint("CK_BackupInvocationAuthorizations_Binding",
                "[CallerIncarnationId] <> '00000000-0000-0000-0000-000000000000' AND "
                + "(([BindingState] = 'Unknown' AND [SessionId] IS NULL AND [SessionEstablishedLocal] IS NULL AND [ConnectionId] IS NULL) OR "
                + "([BindingState] = 'Known' AND [SessionId] IS NOT NULL AND [SessionId] > 0 AND [SessionEstablishedLocal] IS NOT NULL "
                + "AND ([ConnectionId] IS NULL OR ([ConnectionId] IS NOT NULL AND [ConnectionId] <> '00000000-0000-0000-0000-000000000000'))))");
            table.HasCheckConstraint("CK_BackupInvocationAuthorizations_Termination",
                "([TerminalObservedAtUtc] IS NULL AND [TerminationKind] IS NULL AND [TerminationEvidenceId] IS NULL AND [TerminationMutationId] IS NULL) OR "
                + "([TerminalObservedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[TerminalObservedAtUtc]) = 0 "
                + "AND [TerminationKind] IS NOT NULL AND [TerminationKind] IN ('PlatformCompleted','PlatformConfirmedFailed','RecoveredTerminated') "
                + "AND ([TerminationKind] <> 'RecoveredTerminated' OR [BindingState] = 'Known') "
                + "AND [TerminationEvidenceId] IS NOT NULL AND [TerminationEvidenceId] <> '00000000-0000-0000-0000-000000000000' "
                + "AND [TerminationMutationId] IS NOT NULL AND [TerminationMutationId] <> '00000000-0000-0000-0000-000000000000')");
        });
        builder.Property(x => x.TerminationKind).HasConversion<string>().HasMaxLength(30);
        var binding = builder.ComplexProperty(x => x.Binding);
        binding.Property(x => x.State).HasColumnName("BindingState").HasConversion<string>().HasMaxLength(20);
        binding.Property(x => x.CallerIncarnationId).HasColumnName("CallerIncarnationId");
        binding.Property(x => x.SessionId).HasColumnName("SessionId");
        binding.Property(x => x.SessionEstablishedLocal).HasColumnName("SessionEstablishedLocal").HasColumnType("datetime2(7)");
        binding.Property(x => x.ConnectionId).HasColumnName("ConnectionId");
        builder.HasIndex(x => x.DatabaseId).IsUnique().HasFilter("[TerminalObservedAtUtc] IS NULL");
        builder.HasIndex(x => new { x.TaskId, x.AttemptId }).IsUnique();
        builder.HasIndex(x => x.MutationId).IsUnique();
        builder.HasIndex(x => x.TerminationMutationId).IsUnique().HasFilter("[TerminationMutationId] IS NOT NULL");
        builder.HasOne<ManagedDatabase>().WithMany().HasForeignKey(x => x.DatabaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupPlanExecutionOperation>().WithMany().HasForeignKey(x => new { x.TaskId, x.AttemptId, x.SqlOperationId })
            .HasPrincipalKey(x => new { x.TaskId, x.AttemptId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupPlanExecutionOperation>().WithMany().HasForeignKey(x => new { x.TaskId, x.AttemptId, x.TerminationEvidenceId })
            .HasPrincipalKey(x => new { x.TaskId, x.AttemptId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupAttempt>().WithMany().HasForeignKey(x => new { x.TaskId, x.AttemptId })
            .HasPrincipalKey(x => new { x.TaskId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal static class BackupExecutionContractMapping
{
    internal static void MapFacts(ComplexPropertyBuilder<BackupExecutionFacts> builder)
    {
        BackupSetMapping.Map(builder.ComplexProperty(x => x.Metadata));
        BackupSetMapping.MapCompletion(builder.ComplexProperty(x => x.Completion));
        BackupSetMapping.MapAssessment(builder.ComplexProperty(x => x.Assessment));
        var active = builder.ComplexProperty(x => x.ActiveAssessment);
        active.Property(x => x.State).HasColumnName("ActiveAssessmentState").HasConversion<string>().HasMaxLength(20);
        active.Property(x => x.Conclusion).HasColumnName("ActiveConclusion").HasConversion<string>().HasMaxLength(20);
        active.Property(x => x.ReasonCode).HasColumnName("ActiveReasonCode").HasMaxLength(100);
        builder.Property(x => x.Outcome).HasColumnName("Outcome").HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.SqlOutcomeSource).HasColumnName("SqlOutcomeSource").HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.PlatformCompletedAtUtc).HasColumnName("PlatformCompletedAtUtc");
        builder.Property(x => x.EvidenceAtUtc).HasColumnName("EvidenceAtUtc");
        builder.Property(x => x.SqlSuccessObserved).HasColumnName("SqlSuccessObserved");
        builder.Property(x => x.OriginalCallTerminated).HasColumnName("OriginalCallTerminated");
        builder.Property(x => x.OriginalCallerCannotInvoke).HasColumnName("OriginalCallerCannotInvoke");
        builder.Property(x => x.UsedCopyOnly).HasColumnName("UsedCopyOnly");
        builder.Property(x => x.UsedChecksum).HasColumnName("UsedChecksum");
        builder.Property(x => x.UsedCompression).HasColumnName("UsedCompression");
        builder.Property(x => x.ReasonCode).HasColumnName("ReasonCode").HasMaxLength(100);
        builder.Property(x => x.ActualBaseBackupSetId).HasColumnName("ActualBaseBackupSetId");
        var content = builder.ComplexProperty(x => x.Content);
        content.Property(x => x.DigestAlgorithm).HasColumnName("ContentDigestAlgorithm").HasMaxLength(6);
        content.Property(x => x.State).HasColumnName("ContentState").HasConversion<string>().HasMaxLength(20);
        content.Property(x => x.DigestHex).HasColumnName("ContentDigest").HasConversion(
            hex => hex == null ? null : Convert.FromHexString(hex),
            bytes => bytes == null ? null : Convert.ToHexStringLower(bytes)).HasColumnType("binary(32)");
        content.Property(x => x.LengthBytes).HasColumnName("ContentLengthBytes");
        content.Property(x => x.StableObjectId).HasColumnName("StableObjectId").HasMaxLength(256);
        content.Property(x => x.Protection).HasColumnName("ObjectProtection").HasConversion<string>().HasMaxLength(30);
        content.Property(x => x.ReasonCode).HasColumnName("ContentReasonCode").HasMaxLength(100);
    }

    internal static void CheckFacts<TEntity>(TableBuilder<TEntity> table, string name) where TEntity : class
    {
        BackupSetMapping.CheckMetadata(table, name);
        table.HasCheckConstraint($"CK_{name}_Content",
            "([ContentState] = 'Unknown' AND [ContentDigestAlgorithm] IS NULL AND [ContentDigest] IS NULL AND [ContentLengthBytes] IS NULL) OR "
            + "([ContentState] = 'Verified' AND [ContentDigestAlgorithm] IS NOT NULL AND [ContentDigestAlgorithm] = 'SHA256' AND [ContentDigest] IS NOT NULL AND [ContentLengthBytes] IS NOT NULL AND [ContentLengthBytes] > 0)");
        table.HasCheckConstraint($"CK_{name}_Outcome",
            "[Outcome] IN ('Unknown','Succeeded','ConfirmedFailed','Indeterminate','Cancelled') "
            + "AND [SqlOutcomeSource] IN ('Unknown','NotInvoked','PlatformResponse','RecoveredEvidence') "
            + "AND ([SqlOutcomeSource] <> 'NotInvoked' OR [SqlSuccessObserved] = 0) "
            + "AND [ObjectProtection] IN ('Unknown','Unsupported','GuardedUntilCommit')");
        table.HasCheckConstraint($"CK_{name}_PlatformCompletedAtUtc",
            "[PlatformCompletedAtUtc] IS NULL OR ([PlatformCompletedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[PlatformCompletedAtUtc]) = 0 "
            + "AND [SqlOutcomeSource] = 'PlatformResponse' AND [SqlSuccessObserved] = 1)");
        table.HasCheckConstraint($"CK_{name}_EvidenceAtUtc",
            "[EvidenceAtUtc] IS NULL OR ([EvidenceAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[EvidenceAtUtc]) = 0)");
        table.HasCheckConstraint($"CK_{name}_ActiveAssessment",
            "([ActiveAssessmentState] = 'NotApplicable' AND [ActiveConclusion] IS NULL AND [ActiveReasonCode] IS NULL) OR "
            + "([ActiveAssessmentState] = 'Known' AND [ActiveConclusion] IS NOT NULL AND [ActiveConclusion] IN ('Verified','Unmanaged','Mismatch','Unknown') "
            + "AND [ActiveReasonCode] IS NOT NULL AND [ActiveReasonCode] IN ("
            + string.Join(",", BackupSetCodes.BaselineReasons.Order().Select(x => $"'{x}'")) + "))");
    }
}
