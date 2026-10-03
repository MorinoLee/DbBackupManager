using DbBackupManager.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class SmtpSettingsConfiguration : IEntityTypeConfiguration<SmtpSettings>
{
    public void Configure(EntityTypeBuilder<SmtpSettings> builder)
    {
        builder.ToTable("SmtpSettings", table =>
        {
            table.HasCheckConstraint(
                "CK_SmtpSettings_Singleton",
                "[Id] = '8F3C2A10-5D6E-4B91-9C7A-11D000000001'");
            table.HasCheckConstraint("CK_SmtpSettings_Host_NotEmpty", "LEN([Host]) > 0");
            table.HasCheckConstraint("CK_SmtpSettings_FromAddress_NotEmpty", "LEN([FromAddress]) > 0");
            table.HasCheckConstraint(
                "CK_SmtpSettings_Port",
                "[Port] BETWEEN 1 AND 65535");
            table.HasCheckConstraint(
                "CK_SmtpSettings_Timeout",
                "[TimeoutSeconds] BETWEEN 1 AND 300");
            table.HasCheckConstraint(
                "CK_SmtpSettings_SecurityMode",
                "[SecurityMode] IN ('StartTls', 'TlsOnConnect', 'Plaintext')");
            table.HasCheckConstraint(
                "CK_SmtpSettings_ConfigurationSerial",
                "[ConfigurationSerial] >= 1");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Host).HasMaxLength(255).IsRequired();
        builder.Property(x => x.FromAddress).HasMaxLength(320).IsRequired();
        builder.Property(x => x.SecurityMode).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasOne<Domain.Configuration.CredentialReference>()
            .WithMany()
            .HasForeignKey(x => x.CredentialReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.CredentialReferenceId)
            .HasDatabaseName("IX_SmtpSettings_CredentialReferenceId");
    }
}

internal sealed class SmtpRecipientConfiguration : IEntityTypeConfiguration<SmtpRecipient>
{
    public void Configure(EntityTypeBuilder<SmtpRecipient> builder)
    {
        builder.ToTable("SmtpRecipients", table =>
        {
            table.HasCheckConstraint("CK_SmtpRecipients_Address_NotEmpty", "LEN([Address]) > 0");
            table.HasCheckConstraint(
                "CK_SmtpRecipients_NormalizedAddress_NotEmpty",
                "LEN([NormalizedAddress]) > 0");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Address).HasMaxLength(320).IsRequired();
        builder.Property(x => x.NormalizedAddress).HasMaxLength(320).IsRequired();
        builder.HasIndex(x => x.NormalizedAddress)
            .IsUnique()
            .HasDatabaseName("UX_SmtpRecipients_NormalizedAddress");
        builder.HasIndex(x => x.SmtpSettingsId)
            .HasDatabaseName("IX_SmtpRecipients_SmtpSettingsId");
        builder.HasOne<SmtpSettings>()
            .WithMany()
            .HasForeignKey(x => x.SmtpSettingsId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NotificationOutboxConfiguration : IEntityTypeConfiguration<NotificationOutbox>
{
    public void Configure(EntityTypeBuilder<NotificationOutbox> builder)
    {
        builder.ToTable("NotificationOutbox", table =>
        {
            table.HasCheckConstraint(
                "CK_NotificationOutbox_Type",
                "[Type] IN ('TaskFailed', 'TaskIndeterminate', 'RetentionDeleteFailed', 'RetentionMissing', 'AdminTest')");
            table.HasCheckConstraint(
                "CK_NotificationOutbox_SourceKind",
                "[SourceKind] IN ('BackupTask', 'BackupFile', 'AdminRequest')");
            table.HasCheckConstraint(
                "CK_NotificationOutbox_Status",
                "[Status] IN ('Pending', 'Sending', 'Sent', 'SendFailed', 'Discarded')");
            table.HasCheckConstraint(
                "CK_NotificationOutbox_ErrorCode_NotEmpty",
                "LEN([ErrorCode]) > 0");
            table.HasCheckConstraint(
                "CK_NotificationOutbox_SendAttemptCount",
                "[SendAttemptCount] >= 0");
            table.HasCheckConstraint(
                "CK_NotificationOutbox_Identity",
                "([Type] IN ('TaskFailed', 'TaskIndeterminate') AND [SourceKind] = 'BackupTask' "
                + "AND [TaskId] = [SourceId] AND [Stage] IN ('Backup', 'VerifyLocal', 'Transfer', 'ValidateCopy', 'Cleanup')) OR "
                + "([Type] IN ('RetentionDeleteFailed', 'RetentionMissing') AND [SourceKind] = 'BackupFile' "
                + "AND [TaskId] IS NOT NULL AND [Stage] IS NULL) OR "
                + "([Type] = 'AdminTest' AND [SourceKind] = 'AdminRequest' AND [TaskId] IS NULL AND [Stage] IS NULL)");
            table.HasCheckConstraint(
                "CK_NotificationOutbox_SendLease",
                "([Status] = 'Sending' AND [SendLeaseToken] IS NOT NULL "
                + "AND LEN([SendLeaseOwner]) > 0 AND [SendLeaseAcquiredAtUtc] IS NOT NULL "
                + "AND [SendLeaseAcquiredAtUtc] >= [OccurredAtUtc] "
                + "AND [SendLeaseExpiresAtUtc] > [SendLeaseAcquiredAtUtc] "
                + "AND [SendAttemptCount] >= 1 AND [NextAttemptAtUtc] IS NULL AND [SentAtUtc] IS NULL) OR "
                + "([Status] <> 'Sending' AND [SendLeaseToken] IS NULL "
                + "AND [SendLeaseOwner] IS NULL AND [SendLeaseAcquiredAtUtc] IS NULL "
                + "AND [SendLeaseExpiresAtUtc] IS NULL)");
            table.HasCheckConstraint(
                "CK_NotificationOutbox_Outcome",
                "([Status] = 'Pending' AND [LastFailureCode] IS NULL AND [NextAttemptAtUtc] IS NULL "
                + "AND [SentAtUtc] IS NULL) OR "
                + "([Status] = 'Sending' AND [LastFailureCode] IS NULL) OR "
                + "([Status] = 'SendFailed' AND LEN([LastFailureCode]) > 0 "
                + "AND [NextAttemptAtUtc] IS NOT NULL AND [SentAtUtc] IS NULL AND [SendAttemptCount] >= 1) OR "
                + "([Status] = 'Sent' AND [LastFailureCode] IS NULL AND [NextAttemptAtUtc] IS NULL "
                + "AND [SentAtUtc] >= [OccurredAtUtc] AND [SendAttemptCount] >= 1) OR "
                + "([Status] = 'Discarded' AND LEN([LastFailureCode]) > 0 "
                + "AND [NextAttemptAtUtc] IS NULL AND [SentAtUtc] IS NULL)");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.SourceKind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Stage).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.ErrorCode).HasMaxLength(100).IsRequired();
        builder.Property(x => x.LastFailureCode).HasMaxLength(100);
        builder.Property(x => x.SendLeaseOwner).HasMaxLength(200);
        builder.Property(x => x.OccurredAtUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.SendLeaseAcquiredAtUtc).HasPrecision(7);
        builder.Property(x => x.SendLeaseExpiresAtUtc).HasPrecision(7);
        builder.Property(x => x.NextAttemptAtUtc).HasPrecision(7);
        builder.Property(x => x.SentAtUtc).HasPrecision(7);
        builder.HasIndex(x => new { x.MutationId, x.Type })
            .IsUnique()
            .HasDatabaseName("UX_NotificationOutbox_MutationId_Type");
        builder.HasIndex(x => x.SendLeaseToken)
            .IsUnique()
            .HasFilter("[SendLeaseToken] IS NOT NULL")
            .HasDatabaseName("UX_NotificationOutbox_SendLeaseToken");
        builder.HasIndex(x => new { x.Status, x.NextAttemptAtUtc, x.OccurredAtUtc })
            .HasDatabaseName("IX_NotificationOutbox_Claim");
    }
}
