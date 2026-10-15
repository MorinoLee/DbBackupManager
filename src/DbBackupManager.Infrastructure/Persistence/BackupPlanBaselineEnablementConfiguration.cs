using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupPlanBaselineEnablementConfiguration : IEntityTypeConfiguration<BackupPlanBaselineEnablement>
{
    public void Configure(EntityTypeBuilder<BackupPlanBaselineEnablement> builder)
    {
        builder.ToTable("BackupPlanBaselineEnablements", table =>
        {
            table.HasCheckConstraint(
                "CK_BackupPlanBaselineEnablements_Decision",
                "([Decision] = 'Queued' AND [TaskId] IS NOT NULL) OR "
                + "([Decision] IN ('SkippedManagedBaseline', 'SkippedUncertainBaseline', 'SkippedOutstandingFull') "
                + "AND [TaskId] IS NULL)");
            table.HasCheckConstraint("CK_BackupPlanBaselineEnablements_Sequence", "[EnablementSequence] >= 1");
        });
        builder.ConfigureConcurrency();
        builder.Property(item => item.Decision).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(item => item.EnablementSequence).IsRequired();
        builder.HasIndex(item => item.MutationId)
            .IsUnique()
            .HasDatabaseName("UX_BackupPlanBaselineEnablements_MutationId");
        builder.HasIndex(item => new { item.PlanId, item.EnablementSequence })
            .IsUnique()
            .HasDatabaseName("UX_BackupPlanBaselineEnablements_Sequence");
        builder.HasIndex(item => new { item.PlanId, item.PlanVersionId })
            .HasDatabaseName("IX_BackupPlanBaselineEnablements_PlanVersion");
        builder.HasIndex(item => item.TaskId)
            .IsUnique()
            .HasFilter("[TaskId] IS NOT NULL")
            .HasDatabaseName("UX_BackupPlanBaselineEnablements_TaskId");
        builder.HasOne<BackupPlanVersion>()
            .WithMany()
            .HasForeignKey(item => new { item.PlanId, item.PlanVersionId })
            .HasPrincipalKey(item => new { item.PlanId, PlanVersionId = item.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackupTask>()
            .WithMany()
            .HasForeignKey(item => item.TaskId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
