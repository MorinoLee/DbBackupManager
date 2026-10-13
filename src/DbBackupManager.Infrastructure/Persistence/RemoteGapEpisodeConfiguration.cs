using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class RemoteGapEpisodeConfiguration : IEntityTypeConfiguration<RemoteGapEpisode>
{
    public void Configure(EntityTypeBuilder<RemoteGapEpisode> builder)
    {
        builder.ToTable("RemoteGapEpisodes", table =>
        {
            table.HasCheckConstraint("CK_RemoteGapEpisodes_GapCode_NotEmpty", "LEN([GapCode]) > 0");
            table.HasCheckConstraint(
                "CK_RemoteGapEpisodes_RecoveredAt",
                "[RecoveredAtUtc] IS NULL OR [RecoveredAtUtc] >= [OpenedAtUtc]");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.GapCode).HasMaxLength(100).IsRequired();
        builder.Property(x => x.OpenedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.RecoveredAtUtc).HasPrecision(7);
        builder.HasIndex(x => x.NotificationId)
            .IsUnique()
            .HasDatabaseName("UX_RemoteGapEpisodes_NotificationId");
        builder.HasIndex(x => new { x.DatabaseId, x.StorageTargetId, x.GapCode })
            .IsUnique()
            .HasFilter("[RecoveredAtUtc] IS NULL")
            .HasDatabaseName("UX_RemoteGapEpisodes_Open");
        builder.HasOne<ManagedDatabase>()
            .WithMany()
            .HasForeignKey(x => x.DatabaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StorageTarget>()
            .WithMany()
            .HasForeignKey(x => x.StorageTargetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<NotificationOutbox>()
            .WithMany()
            .HasForeignKey(x => x.NotificationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
