using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class LogAssessmentModelConfiguration : IEntityTypeConfiguration<LogAssessmentCommit>,
    IEntityTypeConfiguration<LogObservationEntry>, IEntityTypeConfiguration<LogCoverageEntry>,
    IEntityTypeConfiguration<LogGenerationEvidence>, IEntityTypeConfiguration<LogGenerationFact>
{
    public void Configure(EntityTypeBuilder<LogAssessmentCommit> builder)
    {
        builder.ToTable("LogAssessmentCommits", table =>
        {
            table.HasCheckConstraint("CK_LogAssessmentCommits_Scope",
                "([ScopeKind] = 'Local' AND [StorageTargetId] IS NULL) OR ([ScopeKind] = 'Remote' AND [StorageTargetId] IS NOT NULL)");
            table.HasCheckConstraint("CK_LogAssessmentCommits_SchemaVersion", "[SchemaVersion] >= 1");
            table.HasCheckConstraint("CK_LogAssessmentCommits_ObservedAtUtc", "DATEPART(TZOFFSET,[ObservedAtUtc]) = 0");
            table.HasCheckConstraint("CK_LogAssessmentCommits_PreparedAtUtc", "DATEPART(TZOFFSET,[PreparedAtUtc]) = 0");
            table.HasCheckConstraint("CK_LogAssessmentCommits_CommittedAtUtc", "DATEPART(TZOFFSET,[CommittedAtUtc]) = 0");
        });
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.DatabaseId, x.MutationId });
        builder.Property(x => x.RequestFingerprint).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.PayloadFingerprint).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.ScopeKind).HasMaxLength(20);
        builder.Property(x => x.TechnicalReadiness).HasMaxLength(20);
        builder.Property(x => x.TechnicalReasonCode).HasMaxLength(100);
        builder.Property(x => x.DataStartAvailability).HasMaxLength(20);
        builder.Property(x => x.DataStartReasonCode).HasMaxLength(100);
        builder.Property(x => x.CoverageConclusion).HasMaxLength(20);
        builder.Property(x => x.PriorCallReasonCode).HasMaxLength(100);
        builder.Property(x => x.DatabaseBudgetReasonCode).HasMaxLength(100);
        builder.Property(x => x.GlobalBudgetReasonCode).HasMaxLength(100);
    }

    public void Configure(EntityTypeBuilder<LogObservationEntry> builder)
    {
        builder.ToTable("LogObservationEntries");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.AssessmentId, x.EntryNumber }).IsUnique();
        builder.Property(x => x.Role).HasMaxLength(40);
        builder.Property(x => x.Source).HasMaxLength(20);
        builder.Property(x => x.Status).HasMaxLength(20);
        builder.Property(x => x.FailureCode).HasMaxLength(40);
        builder.Property(x => x.FailurePhase).HasMaxLength(40);
        builder.HasOne<LogAssessmentCommit>().WithMany().HasForeignKey(x => x.AssessmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<LogCoverageEntry> builder)
    {
        builder.ToTable("LogCoverageEntries", table =>
            table.HasCheckConstraint("CK_LogCoverageEntries_Scope",
                "([ScopeKind] = 'Local' AND [StorageTargetId] IS NULL) OR ([ScopeKind] = 'Remote' AND [StorageTargetId] IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.AssessmentId).IsUnique();
        builder.Property(x => x.ScopeKind).HasMaxLength(20);
        builder.Property(x => x.Conclusion).HasMaxLength(30);
        builder.Property(x => x.FrontierLsn).HasPrecision(25, 0);
        builder.Property(x => x.RestorePointLsn).HasPrecision(25, 0);
        builder.HasOne<LogAssessmentCommit>().WithMany().HasForeignKey(x => x.AssessmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<LogGenerationEvidence> builder)
    {
        builder.ToTable("LogGenerationEvidence");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.EvidenceKey).IsUnique();
        builder.HasIndex(x => x.GenerationId);
        builder.Property(x => x.EvidenceKey).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.EvidenceKind).HasMaxLength(30);
        builder.Property(x => x.Status).HasMaxLength(20);
        builder.Property(x => x.RecordedAtUtc).IsRequired();
        builder.HasOne<LogGenerationFact>().WithMany().HasForeignKey(x => x.GenerationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<LogGenerationFact> builder)
    {
        builder.ToTable("LogGenerationFacts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
    }
}
