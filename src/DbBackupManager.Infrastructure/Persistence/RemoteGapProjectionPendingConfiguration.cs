using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class RemoteGapProjectionPendingConfiguration : IEntityTypeConfiguration<RemoteGapProjectionPending>
{
    public void Configure(EntityTypeBuilder<RemoteGapProjectionPending> builder)
    {
        builder.ToTable("RemoteGapProjectionPendings", table =>
        {
            table.HasCheckConstraint(
                "CK_RemoteGapProjectionPendings_Stage",
                "[Stage] IS NULL OR [Stage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Stage).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.RequestedAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(x => x.DatabaseId)
            .IsUnique()
            .HasDatabaseName("UX_RemoteGapProjectionPendings_DatabaseId");
        builder.HasOne<ManagedDatabase>()
            .WithMany()
            .HasForeignKey(x => x.DatabaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Targets)
            .WithOne()
            .HasForeignKey(x => x.PendingId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Targets)
            .HasField("_targets")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class RemoteGapProjectionPendingTargetConfiguration : IEntityTypeConfiguration<RemoteGapProjectionPendingTarget>
{
    public void Configure(EntityTypeBuilder<RemoteGapProjectionPendingTarget> builder)
    {
        builder.ToTable("RemoteGapProjectionPendingTargets");
        builder.HasKey(x => new { x.PendingId, x.StorageTargetId });
        builder.HasOne<StorageTarget>()
            .WithMany()
            .HasForeignKey(x => x.StorageTargetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
