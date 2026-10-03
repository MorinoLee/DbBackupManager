using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class AdminUserConfiguration : IEntityTypeConfiguration<AdminUser>
{
    public void Configure(EntityTypeBuilder<AdminUser> builder)
    {
        builder.ToTable("AdminUsers", table =>
        {
            table.HasCheckConstraint("CK_AdminUsers_Username_NotEmpty", "LEN([Username]) > 0");
            table.HasCheckConstraint(
                "CK_AdminUsers_NormalizedUsername_NotEmpty",
                "LEN([NormalizedUsername]) > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Username).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NormalizedUsername).HasMaxLength(100).IsRequired();
        builder.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.FailedLoginCount).IsRequired();
        builder.Property(x => x.FailedLoginWindowStartedAtUtc).HasPrecision(7);
        builder.Property(x => x.LockoutEndUtc).HasPrecision(7);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.NormalizedUsername)
            .IsUnique()
            .HasDatabaseName("UX_AdminUsers_NormalizedUsername");
    }
}

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("AuditRecords", table =>
        {
            table.HasCheckConstraint("CK_AuditRecords_Action_NotEmpty", "LEN([Action]) > 0");
            table.HasCheckConstraint("CK_AuditRecords_TargetType_NotEmpty", "LEN([TargetType]) > 0");
            table.HasCheckConstraint("CK_AuditRecords_Result_NotEmpty", "LEN([Result]) > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityColumn();
        builder.Property(x => x.Action).HasMaxLength(100).IsRequired();
        builder.Property(x => x.TargetType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.TargetId).HasMaxLength(100);
        builder.Property(x => x.Result).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ReasonCode).HasMaxLength(100);
        builder.Property(x => x.OccurredAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(x => x.OccurredAtUtc)
            .HasDatabaseName("IX_AuditRecords_OccurredAtUtc");
        builder.HasIndex(x => x.ActorAdminUserId)
            .HasDatabaseName("IX_AuditRecords_ActorAdminUserId");
        builder.HasOne<AdminUser>()
            .WithMany()
            .HasForeignKey(x => x.ActorAdminUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
