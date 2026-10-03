using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal static class ConcurrentEntityConfiguration
{
    public static void ConfigureConcurrency<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : ConcurrentEntity
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal sealed class CredentialReferenceConfiguration
    : IEntityTypeConfiguration<CredentialReference>
{
    public void Configure(EntityTypeBuilder<CredentialReference> builder)
    {
        builder.ToTable("CredentialReferences", table =>
        {
            table.HasCheckConstraint("CK_CredentialReferences_Name_NotEmpty", "LEN([Name]) > 0");
            table.HasCheckConstraint(
                "CK_CredentialReferences_NormalizedName_NotEmpty",
                "LEN([NormalizedName]) > 0");
            table.HasCheckConstraint(
                "CK_CredentialReferences_Username_NotEmpty",
                "LEN([Username]) > 0");
            table.HasCheckConstraint(
                "CK_CredentialReferences_ProtectedSecret_NotEmpty",
                "LEN([ProtectedSecret]) > 0");
            table.HasCheckConstraint(
                "CK_CredentialReferences_ProtectionVersion_NotEmpty",
                "LEN([ProtectionVersion]) > 0");
            table.HasCheckConstraint(
                "CK_CredentialReferences_Kind",
                "[Kind] IN ('SqlPassword', 'SmbPassword', 'SftpPassword', 'SftpPrivateKey', 'SmtpPassword')");
            table.HasCheckConstraint(
                "CK_CredentialReferences_SecondarySecret",
                "[Kind] = 'SftpPrivateKey' OR [ProtectedSecondarySecret] IS NULL");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.Username).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ProtectedSecret).HasMaxLength(65_535).IsRequired();
        builder.Property(x => x.ProtectedSecondarySecret).HasMaxLength(65_535);
        builder.Property(x => x.ProtectionVersion).HasMaxLength(50).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.HasIndex(x => x.NormalizedName)
            .IsUnique()
            .HasDatabaseName("UX_CredentialReferences_NormalizedName");
    }
}

internal sealed class DatabaseServerConfiguration : IEntityTypeConfiguration<DatabaseServer>
{
    public void Configure(EntityTypeBuilder<DatabaseServer> builder)
    {
        builder.ToTable("DatabaseServers", table =>
        {
            table.HasCheckConstraint("CK_DatabaseServers_Name_NotEmpty", "LEN([Name]) > 0");
            table.HasCheckConstraint(
                "CK_DatabaseServers_NormalizedName_NotEmpty",
                "LEN([NormalizedName]) > 0");
            table.HasCheckConstraint(
                "CK_DatabaseServers_LocalBackupRootPath_NotEmpty",
                "LEN([LocalBackupRootPath]) > 0");
            table.HasCheckConstraint(
                "CK_DatabaseServers_StagingAccess",
                "([StagingAccessProtocol] = 'Smb' AND [StagingAccessPort] IS NULL "
                + "AND [StagingSftpHostKeyFingerprint] IS NULL) OR "
                + "([StagingAccessProtocol] = 'Sftp' AND [StagingAccessPort] BETWEEN 1 AND 65535 "
                + "AND LEN([StagingSftpHostKeyFingerprint]) > 0)");
            table.HasCheckConstraint(
                "CK_DatabaseServers_StagingHost_NotEmpty",
                "LEN([StagingAccessHost]) > 0");
            table.HasCheckConstraint(
                "CK_DatabaseServers_StagingBasePath_NotEmpty",
                "LEN([StagingAccessBasePath]) > 0");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.LocalBackupRootPath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.StagingAccessProtocol)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();
        builder.Property(x => x.StagingAccessHost).HasMaxLength(255).IsRequired();
        builder.Property(x => x.StagingAccessBasePath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.StagingSftpHostKeyFingerprint).HasMaxLength(500);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.HasIndex(x => x.NormalizedName)
            .IsUnique()
            .HasDatabaseName("UX_DatabaseServers_NormalizedName");
        builder.HasIndex(x => x.StagingCredentialReferenceId)
            .HasDatabaseName("IX_DatabaseServers_StagingCredentialReferenceId");
        builder.HasOne<CredentialReference>()
            .WithMany()
            .HasForeignKey(x => x.StagingCredentialReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DatabaseInstanceConfiguration : IEntityTypeConfiguration<DatabaseInstance>
{
    public void Configure(EntityTypeBuilder<DatabaseInstance> builder)
    {
        builder.ToTable("DatabaseInstances", table =>
        {
            table.HasCheckConstraint("CK_DatabaseInstances_Name_NotEmpty", "LEN([Name]) > 0");
            table.HasCheckConstraint(
                "CK_DatabaseInstances_NormalizedName_NotEmpty",
                "LEN([NormalizedName]) > 0");
            table.HasCheckConstraint(
                "CK_DatabaseInstances_ConnectionAddress_NotEmpty",
                "LEN([ConnectionAddress]) > 0 AND LEN([NormalizedConnectionAddress]) > 0");
            table.HasCheckConstraint(
                "CK_DatabaseInstances_EncryptConnection",
                "[EncryptConnection] = CAST(1 AS bit)");
            table.HasCheckConstraint(
                "CK_DatabaseInstances_CertificateTrustReason",
                "([TrustServerCertificate] = CAST(1 AS bit) AND LEN([CertificateTrustReason]) > 0) "
                + "OR ([TrustServerCertificate] = CAST(0 AS bit) AND [CertificateTrustReason] IS NULL)");
            table.HasCheckConstraint("CK_DatabaseInstances_LegacyTls",
                "([AllowLegacyTls] = 1 AND [LegacyTlsReason] IS NOT NULL AND LEN(LTRIM(RTRIM([LegacyTlsReason]))) > 0) OR ([AllowLegacyTls] = 0 AND [LegacyTlsReason] IS NULL)");
            table.HasCheckConstraint(
                "CK_DatabaseInstances_ConnectionTimeoutSeconds",
                "[ConnectionTimeoutSeconds] BETWEEN 1 AND 300");
            table.HasCheckConstraint(
                "CK_DatabaseInstances_ConnectionStatus",
                "[ConnectionStatus] IN ('Unknown', 'Connected', 'Failed')");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ConnectionAddress).HasMaxLength(255).IsRequired();
        builder.Property(x => x.NormalizedConnectionAddress).HasMaxLength(255).IsRequired();
        builder.Property(x => x.EncryptConnection).IsRequired();
        builder.Property(x => x.TrustServerCertificate).IsRequired();
        builder.Property(x => x.CertificateTrustReason).HasMaxLength(500);
        builder.Property(x => x.AllowLegacyTls).IsRequired();
        builder.Property(x => x.LegacyTlsReason).HasMaxLength(500);
        builder.Property(x => x.ConnectionStatus)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(x => x.ProductVersion).HasMaxLength(100);
        builder.Property(x => x.ProductLevel).HasMaxLength(100);
        builder.Property(x => x.Edition).HasMaxLength(300);
        builder.Property(x => x.LastConnectedAtUtc).HasPrecision(7);
        builder.Property(x => x.LastConnectionCheckedAtUtc).HasPrecision(7);
        builder.Property(x => x.LastConnectionErrorCode).HasMaxLength(100);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.HasIndex(x => new { x.ServerId, x.NormalizedName })
            .IsUnique()
            .HasDatabaseName("UX_DatabaseInstances_ServerId_NormalizedName");
        builder.HasIndex(x => x.NormalizedConnectionAddress)
            .IsUnique()
            .HasDatabaseName("UX_DatabaseInstances_NormalizedConnectionAddress");
        builder.HasIndex(x => x.SqlCredentialReferenceId)
            .HasDatabaseName("IX_DatabaseInstances_SqlCredentialReferenceId");
        builder.HasOne<DatabaseServer>()
            .WithMany()
            .HasForeignKey(x => x.ServerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CredentialReference>()
            .WithMany()
            .HasForeignKey(x => x.SqlCredentialReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ManagedDatabaseConfiguration : IEntityTypeConfiguration<ManagedDatabase>
{
    public void Configure(EntityTypeBuilder<ManagedDatabase> builder)
    {
        builder.ToTable("ManagedDatabases", table =>
        {
            table.HasCheckConstraint(
                "CK_ManagedDatabases_DatabaseName_NotEmpty",
                "LEN([DatabaseName]) > 0 AND LEN([NormalizedDatabaseName]) > 0");
            table.HasCheckConstraint(
                "CK_ManagedDatabases_SystemDatabaseNotManaged",
                "[IsSystemDatabase] = CAST(0 AS bit) OR [IsManaged] = CAST(0 AS bit)");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.DatabaseName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.NormalizedDatabaseName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RecoveryModel).HasMaxLength(60);
        builder.Property(x => x.StateDescription).HasMaxLength(60);
        builder.Property(x => x.IsSystemDatabase).IsRequired();
        builder.Property(x => x.IsManaged).IsRequired();
        builder.Property(x => x.IsAvailable).IsRequired();
        builder.Property(x => x.LastDiscoveredAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(x => new { x.InstanceId, x.NormalizedDatabaseName })
            .IsUnique()
            .HasDatabaseName("UX_ManagedDatabases_InstanceId_NormalizedDatabaseName");
        builder.HasOne<DatabaseInstance>()
            .WithMany()
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StorageTargetConfiguration : IEntityTypeConfiguration<StorageTarget>
{
    public void Configure(EntityTypeBuilder<StorageTarget> builder)
    {
        builder.ToTable("StorageTargets", table =>
        {
            table.HasCheckConstraint("CK_StorageTargets_Name_NotEmpty", "LEN([Name]) > 0");
            table.HasCheckConstraint(
                "CK_StorageTargets_NormalizedName_NotEmpty",
                "LEN([NormalizedName]) > 0");
            table.HasCheckConstraint("CK_StorageTargets_Host_NotEmpty", "LEN([Host]) > 0");
            table.HasCheckConstraint("CK_StorageTargets_BasePath_NotEmpty", "LEN([BasePath]) > 0");
            table.HasCheckConstraint(
                "CK_StorageTargets_Endpoint",
                "([Protocol] = 'Smb' AND [Port] IS NULL AND [SftpHostKeyFingerprint] IS NULL) OR "
                + "([Protocol] = 'Sftp' AND [Port] BETWEEN 1 AND 65535 "
                + "AND LEN([SftpHostKeyFingerprint]) > 0)");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Protocol).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.Host).HasMaxLength(255).IsRequired();
        builder.Property(x => x.BasePath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.SftpHostKeyFingerprint).HasMaxLength(500);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.HasIndex(x => x.NormalizedName)
            .IsUnique()
            .HasDatabaseName("UX_StorageTargets_NormalizedName");
        builder.HasIndex(x => x.CredentialReferenceId)
            .HasDatabaseName("IX_StorageTargets_CredentialReferenceId");
        builder.HasOne<CredentialReference>()
            .WithMany()
            .HasForeignKey(x => x.CredentialReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupPolicyConfiguration : IEntityTypeConfiguration<BackupPolicy>
{
    public void Configure(EntityTypeBuilder<BackupPolicy> builder)
    {
        builder.ToTable("BackupPolicies", table =>
        {
            table.HasCheckConstraint("CK_BackupPolicies_Name_NotEmpty", "LEN([Name]) > 0");
            table.HasCheckConstraint(
                "CK_BackupPolicies_NormalizedName_NotEmpty",
                "LEN([NormalizedName]) > 0");
            table.HasCheckConstraint("CK_BackupPolicies_BackupType", "[BackupType] = 'Full'");
            table.HasCheckConstraint(
                "CK_BackupPolicies_Schedule",
                "([ScheduleType] = 'Daily' AND [DaysOfWeek] = 0) OR "
                + "([ScheduleType] = 'Weekly' AND [DaysOfWeek] BETWEEN 1 AND 127)");
            table.HasCheckConstraint(
                "CK_BackupPolicies_Storage",
                "([StorageMode] = 'LocalOnly' AND [StorageTargetId] IS NULL "
                + "AND [LocalRetentionDays] BETWEEN 1 AND 36500 AND [RemoteRetentionDays] IS NULL) OR "
                + "([StorageMode] = 'LocalAndRemote' AND [StorageTargetId] IS NOT NULL "
                + "AND [LocalRetentionDays] BETWEEN 1 AND 36500 "
                + "AND [RemoteRetentionDays] BETWEEN 1 AND 36500) OR "
                + "([StorageMode] = 'RemoteOnly' AND [StorageTargetId] IS NOT NULL "
                + "AND [LocalRetentionDays] IS NULL AND [RemoteRetentionDays] BETWEEN 1 AND 36500)");
            table.HasCheckConstraint(
                "CK_BackupPolicies_Timeouts",
                "[BackupTimeoutMinutes] BETWEEN 1 AND 1440 "
                + "AND [VerifyTimeoutMinutes] BETWEEN 1 AND 1440 "
                + "AND [TransferTimeoutMinutes] BETWEEN 1 AND 1440");
            table.HasCheckConstraint(
                "CK_BackupPolicies_TimeZoneId_NotEmpty",
                "LEN([TimeZoneId]) > 0");
            table.HasCheckConstraint(
                "CK_BackupPolicies_ScheduleEffectiveFrom",
                "([IsManualOnly] = 1 AND [ScheduleEffectiveFromUtc] IS NULL) OR "
                + "([IsManualOnly] = 0 AND [IsEnabled] = 0 AND [ScheduleEffectiveFromUtc] IS NULL) OR "
                + "([IsManualOnly] = 0 AND [IsEnabled] = 1 AND [ScheduleEffectiveFromUtc] IS NOT NULL)");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.BackupType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.StorageMode)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();
        builder.Property(x => x.ScheduleType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(x => x.LocalTime).HasColumnType("time(0)").IsRequired();
        builder.Property(x => x.DaysOfWeek).HasConversion<int>().IsRequired();
        builder.Property(x => x.TimeZoneId).HasMaxLength(150).IsRequired();
        builder.Property(x => x.UseChecksum).IsRequired();
        builder.Property(x => x.UseCompression).IsRequired();
        builder.Property(x => x.UseCopyOnly).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.ScheduleEffectiveFromUtc).HasColumnType("datetimeoffset(7)");
        builder.HasIndex(x => x.NormalizedName)
            .IsUnique()
            .HasDatabaseName("UX_BackupPolicies_NormalizedName");
        builder.HasIndex(x => new { x.DatabaseId, x.BackupType })
            .IsUnique()
            .HasFilter("[IsEnabled] = 1")
            .HasDatabaseName("UX_BackupPolicies_DatabaseId_BackupType_Enabled");
        builder.HasIndex(x => x.StorageTargetId)
            .HasDatabaseName("IX_BackupPolicies_StorageTargetId");
        builder.HasOne<ManagedDatabase>()
            .WithMany()
            .HasForeignKey(x => x.DatabaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StorageTarget>()
            .WithMany()
            .HasForeignKey(x => x.StorageTargetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
