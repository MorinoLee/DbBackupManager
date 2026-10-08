using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupSetConfiguration : IEntityTypeConfiguration<BackupSet>
{
    public void Configure(EntityTypeBuilder<BackupSet> builder)
    {
        builder.ConfigureConcurrency();
        builder.ToTable("BackupSets", table =>
        {
            BackupSetMapping.CheckMetadata(table);
            table.HasCheckConstraint("CK_BackupSets_ReconciliationCount", "[ReconciliationCount] >= 1");
            table.HasCheckConstraint("CK_BackupSets_Dependency",
                "[BaseBackupSetId] IS NULL OR ([BaseBackupSetId] IS NOT NULL AND [BaseBackupSetId] <> [Id] "
                + "AND [TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] = 'Differential')");
            table.HasCheckConstraint("CK_BackupSets_Conflict",
                "[HasMetadataConflict] = 0 OR ([HasMetadataConflict] = 1 AND [Conclusion] IS NOT NULL "
                + "AND [Conclusion] = 'Mismatch' AND [BaselineReasonCode] IS NOT NULL "
                + "AND [BaselineReasonCode] = 'baseline.source_conflict')");
        });
        BackupSetMapping.Map(builder.ComplexProperty(x => x.Metadata));
        BackupSetMapping.MapCompletion(builder.ComplexProperty(x => x.Completion));
        BackupSetMapping.MapAssessment(builder.ComplexProperty(x => x.Assessment));
        // EF 的复合属性不能直接作为索引键，用确定性计算列为已确认 GUID 建唯一索引。
        builder.Property<Guid?>("BackupSetGuidIndex").HasComputedColumnSql("[BackupSetGuid]", stored: true);
        builder.HasIndex("BackupSetGuidIndex").IsUnique().HasFilter("[BackupSetGuid] IS NOT NULL");
        builder.HasIndex(x => x.AttemptId).IsUnique();
        builder.HasAlternateKey(x => new { x.TaskId, x.AttemptId, x.DatabaseId, x.Id });
        builder.HasAlternateKey(x => new { x.TaskId, x.AttemptId, x.Id });
        builder.HasAlternateKey(x => new { x.DatabaseId, x.Id });
        builder.HasOne<BackupAttempt>().WithMany()
            .HasForeignKey(x => new { x.TaskId, x.AttemptId })
            .HasPrincipalKey(x => new { x.TaskId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ManagedDatabase>().WithMany().HasForeignKey(x => x.DatabaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupSet>().WithMany()
            .HasForeignKey(x => new { x.DatabaseId, x.BaseBackupSetId })
            .HasPrincipalKey(x => new { x.DatabaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupSetEvidenceConfiguration : IEntityTypeConfiguration<BackupSetEvidence>
{
    public void Configure(EntityTypeBuilder<BackupSetEvidence> builder)
    {
        builder.HasKey(x => x.Id);
        builder.ToTable("BackupSetEvidence", table =>
        {
            BackupSetMapping.CheckMetadata(table);
            table.HasCheckConstraint("CK_BackupSetEvidence_Sequence", "[ReconciliationNumber] >= 1 AND [EntryNumber] >= 0");
            table.HasCheckConstraint("CK_BackupSetEvidence_Source", "[Source] IN ('Comparison','BackupHeader','Msdb','Database','Registration')");
            table.HasCheckConstraint("CK_BackupSetEvidence_Kind", "[Kind] IN ('Backup','Dependency','ActiveBaseline')");
            table.HasCheckConstraint("CK_BackupSetEvidence_ObservedAtUtc", "DATEPART(TZOFFSET,[ObservedAtUtc]) = 0");
        });
        BackupSetMapping.Map(builder.ComplexProperty(x => x.Metadata));
        BackupSetMapping.MapCompletion(builder.ComplexProperty(x => x.Completion));
        BackupSetMapping.MapAssessment(builder.ComplexProperty(x => x.Assessment));
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(x => new { x.AttemptId, x.ReconciliationNumber, x.EntryNumber }).IsUnique();
        builder.HasIndex(x => new { x.TaskId, x.AttemptId, x.MutationId, x.EntryNumber }).IsUnique();
        builder.HasOne<BackupAttempt>().WithMany()
            .HasForeignKey(x => new { x.TaskId, x.AttemptId })
            .HasPrincipalKey(x => new { x.TaskId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupSet>().WithMany()
            .HasForeignKey(x => new { x.TaskId, x.AttemptId, x.BackupSetId })
            .HasPrincipalKey(x => new { x.TaskId, x.AttemptId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupSet>().WithMany()
            .HasForeignKey(x => x.RequestedBaseBackupSetId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal static class BackupSetMapping
{
    internal static void Map(ComplexPropertyBuilder<BackupSetMetadata> builder)
    {
        Field(builder.ComplexProperty(x => x.BackupSetGuid), "BackupSetGuid");
        Field(builder.ComplexProperty(x => x.DatabaseGuid), "DatabaseGuid");
        Field(builder.ComplexProperty(x => x.FamilyGuid), "FamilyGuid");
        Field(builder.ComplexProperty(x => x.Type), "Type");
        Field(builder.ComplexProperty(x => x.FirstLsn), "FirstLsn");
        Field(builder.ComplexProperty(x => x.LastLsn), "LastLsn");
        Field(builder.ComplexProperty(x => x.CheckpointLsn), "CheckpointLsn");
        Field(builder.ComplexProperty(x => x.DatabaseBackupLsn), "DatabaseBackupLsn");
        Field(builder.ComplexProperty(x => x.DifferentialBaseLsn), "DifferentialBaseLsn");
        Field(builder.ComplexProperty(x => x.DifferentialBaseGuid), "DifferentialBaseGuid");
        Field(builder.ComplexProperty(x => x.FirstRecoveryForkId), "FirstRecoveryForkId");
        Field(builder.ComplexProperty(x => x.RecoveryForkId), "RecoveryForkId");
        Field(builder.ComplexProperty(x => x.ForkPointLsn), "ForkPointLsn");
        Field(builder.ComplexProperty(x => x.IsCopyOnly), "IsCopyOnly");
        Field(builder.ComplexProperty(x => x.HasBackupChecksums), "HasBackupChecksums");
        Field(builder.ComplexProperty(x => x.IsCompressed), "IsCompressed");
        Field(builder.ComplexProperty(x => x.IsDamaged), "IsDamaged");
        Field(builder.ComplexProperty(x => x.IsSnapshot), "IsSnapshot");
        Field(builder.ComplexProperty(x => x.HasIncompleteMetadata), "HasIncompleteMetadata");
        Field(builder.ComplexProperty(x => x.SqlStartedLocal), "SqlStartedLocal");
        Field(builder.ComplexProperty(x => x.SqlFinishedLocal), "SqlFinishedLocal");
    }

    private static void Field<T>(ComplexPropertyBuilder<BackupMetadataField<T>> builder, string name) where T : struct
    {
        builder.Property(x => x.State).HasColumnName(name + "State").HasConversion<string>().HasMaxLength(20);
        var value = builder.Property(x => x.Value).HasColumnName(name);
        if (typeof(T) == typeof(BackupLsn))
            value.HasConversion(
                new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<BackupLsn?, decimal?>(
                    lsn => lsn.HasValue ? lsn.Value.Value : null,
                    number => number.HasValue ? new BackupLsn(number.Value) : null))
                .HasColumnType("numeric(25,0)");
        else if (typeof(T) == typeof(BackupType)) value.HasConversion<string>().HasMaxLength(20);
        else if (typeof(T) == typeof(DateTime)) value.HasColumnType("datetime2(7)");
    }

    internal static void MapCompletion(ComplexPropertyBuilder<BackupCompletionTimeDecision> builder)
    {
        builder.Property(x => x.CompletedAtUtc).HasColumnName("CompletedAtUtc");
        builder.Property(x => x.Source).HasColumnName("CompletionSource").HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ReasonCode).HasColumnName("CompletionReasonCode").HasMaxLength(100);
    }

    internal static void MapAssessment(ComplexPropertyBuilder<BackupSetAssessment> builder)
    {
        builder.Property(x => x.State).HasColumnName("AssessmentState").HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Conclusion).HasColumnName("Conclusion").HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ReasonCode).HasColumnName("BaselineReasonCode").HasMaxLength(100);
    }

    internal static void CheckMetadata<TEntity>(TableBuilder<TEntity> table, string? tableName = null) where TEntity : class
    {
        var name = tableName ?? (typeof(TEntity) == typeof(BackupSet) ? "BackupSets" : "BackupSetEvidence");
        table.HasCheckConstraint($"CK_{name}_BackupSetGuid", "([BackupSetGuidState] = 'Known' AND [BackupSetGuid] IS NOT NULL AND [BackupSetGuid] <> '00000000-0000-0000-0000-000000000000') "
            + "OR ([BackupSetGuidState] IN ('Unknown') AND [BackupSetGuid] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_DatabaseGuid", "([DatabaseGuidState] = 'Known' AND [DatabaseGuid] IS NOT NULL AND [DatabaseGuid] <> '00000000-0000-0000-0000-000000000000') "
            + "OR ([DatabaseGuidState] IN ('Unknown') AND [DatabaseGuid] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_FamilyGuid", "([FamilyGuidState] = 'Known' AND [FamilyGuid] IS NOT NULL AND [FamilyGuid] <> '00000000-0000-0000-0000-000000000000') "
            + "OR ([FamilyGuidState] IN ('Unknown') AND [FamilyGuid] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_Type", "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Differential','Log')) "
            + "OR ([TypeState] IN ('Unknown') AND [Type] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_FirstLsn", "([FirstLsnState] = 'Known' AND [FirstLsn] IS NOT NULL AND [FirstLsn] >= 0) "
            + "OR ([FirstLsnState] IN ('Unknown') AND [FirstLsn] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_LastLsn", "([LastLsnState] = 'Known' AND [LastLsn] IS NOT NULL AND [LastLsn] >= 0) "
            + "OR ([LastLsnState] IN ('Unknown') AND [LastLsn] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_CheckpointLsn", "([CheckpointLsnState] = 'Known' AND [CheckpointLsn] IS NOT NULL AND [CheckpointLsn] >= 0) "
            + "OR ([CheckpointLsnState] IN ('Unknown') AND [CheckpointLsn] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_DatabaseBackupLsn", "([DatabaseBackupLsnState] = 'Known' AND [DatabaseBackupLsn] IS NOT NULL AND [DatabaseBackupLsn] >= 0) "
            + "OR ([DatabaseBackupLsnState] IN ('Unknown') AND [DatabaseBackupLsn] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_DifferentialBaseLsn", "([DifferentialBaseLsnState] = 'Known' AND [DifferentialBaseLsn] IS NOT NULL AND [DifferentialBaseLsn] >= 0) "
            + "OR ([DifferentialBaseLsnState] IN ('Unknown','NotApplicable') AND [DifferentialBaseLsn] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_DifferentialBaseGuid", "([DifferentialBaseGuidState] = 'Known' AND [DifferentialBaseGuid] IS NOT NULL AND [DifferentialBaseGuid] <> '00000000-0000-0000-0000-000000000000') "
            + "OR ([DifferentialBaseGuidState] IN ('Unknown','NotApplicable') AND [DifferentialBaseGuid] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_FirstRecoveryForkId", "([FirstRecoveryForkIdState] = 'Known' AND [FirstRecoveryForkId] IS NOT NULL AND [FirstRecoveryForkId] <> '00000000-0000-0000-0000-000000000000') "
            + "OR ([FirstRecoveryForkIdState] IN ('Unknown') AND [FirstRecoveryForkId] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_RecoveryForkId", "([RecoveryForkIdState] = 'Known' AND [RecoveryForkId] IS NOT NULL AND [RecoveryForkId] <> '00000000-0000-0000-0000-000000000000') "
            + "OR ([RecoveryForkIdState] IN ('Unknown') AND [RecoveryForkId] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_ForkPointLsn", "([ForkPointLsnState] = 'Known' AND [ForkPointLsn] IS NOT NULL AND [ForkPointLsn] >= 0) "
            + "OR ([ForkPointLsnState] IN ('Unknown','NotApplicable') AND [ForkPointLsn] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_IsCopyOnly", "([IsCopyOnlyState] = 'Known' AND [IsCopyOnly] IS NOT NULL) "
            + "OR ([IsCopyOnlyState] IN ('Unknown') AND [IsCopyOnly] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_HasBackupChecksums", "([HasBackupChecksumsState] = 'Known' AND [HasBackupChecksums] IS NOT NULL) "
            + "OR ([HasBackupChecksumsState] IN ('Unknown') AND [HasBackupChecksums] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_IsCompressed", "([IsCompressedState] = 'Known' AND [IsCompressed] IS NOT NULL) "
            + "OR ([IsCompressedState] IN ('Unknown') AND [IsCompressed] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_IsDamaged", "([IsDamagedState] = 'Known' AND [IsDamaged] IS NOT NULL) "
            + "OR ([IsDamagedState] IN ('Unknown') AND [IsDamaged] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_IsSnapshot", "([IsSnapshotState] = 'Known' AND [IsSnapshot] IS NOT NULL) "
            + "OR ([IsSnapshotState] IN ('Unknown') AND [IsSnapshot] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_HasIncompleteMetadata", "([HasIncompleteMetadataState] = 'Known' AND [HasIncompleteMetadata] IS NOT NULL) "
            + "OR ([HasIncompleteMetadataState] IN ('Unknown') AND [HasIncompleteMetadata] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_SqlStartedLocal", "([SqlStartedLocalState] = 'Known' AND [SqlStartedLocal] IS NOT NULL) "
            + "OR ([SqlStartedLocalState] IN ('Unknown') AND [SqlStartedLocal] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_SqlFinishedLocal", "([SqlFinishedLocalState] = 'Known' AND [SqlFinishedLocal] IS NOT NULL) "
            + "OR ([SqlFinishedLocalState] IN ('Unknown') AND [SqlFinishedLocal] IS NULL)");
        table.HasCheckConstraint($"CK_{name}_DifferentialFields",
            "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] IN ('Full','Log') "
            + "AND [DifferentialBaseLsnState] = 'NotApplicable' AND [DifferentialBaseGuidState] = 'NotApplicable') OR "
            + "([TypeState] = 'Known' AND [Type] IS NOT NULL AND [Type] = 'Differential' "
            + "AND [DifferentialBaseLsnState] <> 'NotApplicable' AND [DifferentialBaseGuidState] <> 'NotApplicable') OR "
            + "([TypeState] = 'Unknown' AND [Type] IS NULL AND [DifferentialBaseLsnState] <> 'NotApplicable' "
            + "AND [DifferentialBaseGuidState] <> 'NotApplicable')");
        table.HasCheckConstraint($"CK_{name}_Assessment",
            "([AssessmentState] = 'NotApplicable' AND [Conclusion] IS NULL AND [BaselineReasonCode] IS NULL) OR "
            + "([AssessmentState] = 'Known' AND [Conclusion] IS NOT NULL AND [Conclusion] IN ('Verified','Unmanaged','Mismatch','Unknown') "
            + "AND [BaselineReasonCode] IS NOT NULL AND [BaselineReasonCode] IN ("
            + string.Join(",", BackupSetCodes.BaselineReasons.Order().Select(x => $"'{x}'")) + "))");
        table.HasCheckConstraint($"CK_{name}_Completion",
            "([CompletionSource] = 'PlatformObserved' AND [CompletedAtUtc] IS NOT NULL "
            + "AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.platform_observed') OR "
            + "([CompletionSource] = 'SqlLocalTime' AND [CompletedAtUtc] IS NOT NULL "
            + "AND DATEPART(TZOFFSET,[CompletedAtUtc]) = 0 AND [CompletionReasonCode] = 'completion.sql_local_converted') OR "
            + "([CompletionSource] = 'Unknown' AND [CompletedAtUtc] IS NULL AND [CompletionReasonCode] IN ("
            + string.Join(",", BackupSetCodes.UnknownCompletionReasons.Order().Select(x => $"'{x}'")) + "))");
    }
}

internal static class BackupSetMigrationGuard
{
    internal const string RejectDownWhenDataExists = """
        IF EXISTS (SELECT 1 FROM [BackupSets]) OR EXISTS (SELECT 1 FROM [BackupSetEvidence])
        BEGIN
            THROW 51001, N'已登记备份集或核对证据，不能回退该迁移。请从平台库备份恢复。', 1;
        END
        """;
}
