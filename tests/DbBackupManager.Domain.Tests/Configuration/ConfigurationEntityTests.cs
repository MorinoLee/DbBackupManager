using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.Configuration;

public sealed class ConfigurationEntityTests
{
    [Fact]
    public void CredentialReferenceStoresOnlyProtectedValuesAndNormalizesName()
    {
        var credential = new CredentialReference(
            Guid.NewGuid(),
            "  SQL 备份账号  ",
            CredentialKind.SqlPassword,
            "backup-user",
            "protected:synthetic-secret",
            "dp-v1");

        Assert.Equal("SQL 备份账号", credential.Name);
        Assert.Equal("SQL 备份账号", credential.NormalizedName);
        Assert.Equal("protected:synthetic-secret", credential.ProtectedSecret);
        Assert.Null(credential.ProtectedSecondarySecret);
        Assert.True(credential.IsEnabled);
    }

    [Fact]
    public void SftpPrivateKeyAllowsProtectedPassphrase()
    {
        var credential = new CredentialReference(
            Guid.NewGuid(),
            "SFTP 私钥",
            CredentialKind.SftpPrivateKey,
            "transfer-user",
            "protected:synthetic-private-key",
            "dp-v1",
            "protected:synthetic-passphrase");

        Assert.Equal("protected:synthetic-passphrase", credential.ProtectedSecondarySecret);
    }

    [Fact]
    public void NonPrivateKeyCredentialRejectsSecondarySecret()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new CredentialReference(
                Guid.NewGuid(),
                "SMB 账号",
                CredentialKind.SmbPassword,
                "file-user",
                "protected:synthetic-secret",
                "dp-v1",
                "protected:unexpected-secondary"));
    }

    [Fact]
    public void InvalidCredentialRotationDoesNotChangeExistingProtectedValues()
    {
        var credential = new CredentialReference(
            Guid.NewGuid(),
            "SQL 账号",
            CredentialKind.SqlPassword,
            "backup-user",
            "protected:old-secret",
            "dp-v1");

        Assert.Throws<ArgumentException>(() =>
            credential.RotateProtectedValues(
                "protected:new-secret",
                "dp-v2",
                "protected:invalid-secondary"));

        Assert.Equal("protected:old-secret", credential.ProtectedSecret);
        Assert.Equal("dp-v1", credential.ProtectionVersion);
    }

    [Fact]
    public void SmbEndpointRejectsPortAndHostKeyFingerprint()
    {
        var credentialId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            CreateServer(new FileEndpointSettings(
                FileTransferProtocol.Smb,
                "synthetic-smb-host",
                445,
                "synthetic-share",
                credentialId,
                null)));
        Assert.Throws<ArgumentException>(() =>
            CreateServer(new FileEndpointSettings(
                FileTransferProtocol.Smb,
                "synthetic-smb-host",
                null,
                "synthetic-share",
                credentialId,
                "synthetic-fingerprint")));
    }

    [Theory]
    [InlineData(null, "synthetic-fingerprint")]
    [InlineData(22, null)]
    [InlineData(0, "synthetic-fingerprint")]
    public void SftpEndpointRequiresValidPortAndHostKeyFingerprint(
        int? port,
        string? fingerprint)
    {
        var endpoint = new FileEndpointSettings(
            FileTransferProtocol.Sftp,
            "synthetic-sftp-host",
            port,
            "/synthetic/root",
            Guid.NewGuid(),
            fingerprint);

        Assert.ThrowsAny<ArgumentException>(() => CreateServer(endpoint));
    }

    [Fact]
    public void DatabaseServerCapturesValidatedStagingMapping()
    {
        var credentialId = Guid.NewGuid();
        var server = CreateServer(new FileEndpointSettings(
            FileTransferProtocol.Sftp,
            "synthetic-sftp-host",
            22,
            "/synthetic/root",
            credentialId,
            "SHA256:synthetic-fingerprint"));

        Assert.Equal(FileTransferProtocol.Sftp, server.StagingAccessProtocol);
        Assert.Equal(22, server.StagingAccessPort);
        Assert.Equal(credentialId, server.StagingCredentialReferenceId);
        Assert.Equal("SHA256:synthetic-fingerprint", server.StagingSftpHostKeyFingerprint);
    }

    [Fact]
    public void InvalidServerUpdateDoesNotPartiallyChangeNameOrPath()
    {
        var server = CreateServer(new FileEndpointSettings(
            FileTransferProtocol.Smb,
            "synthetic-smb-host",
            null,
            "synthetic-share",
            Guid.NewGuid(),
            null));

        Assert.ThrowsAny<ArgumentException>(() =>
            server.Update(
                "changed-name",
                "changed-root",
                new FileEndpointSettings(
                    FileTransferProtocol.Sftp,
                    "synthetic-sftp-host",
                    22,
                    "/changed/root",
                    Guid.NewGuid(),
                    null),
                null));

        Assert.Equal("合成数据库服务器", server.Name);
        Assert.Equal("synthetic-sql-local-root", server.LocalBackupRootPath);
    }

    [Fact]
    public void DatabaseInstanceRejectsUnencryptedConnection()
    {
        Assert.Throws<ArgumentException>(() =>
            new DatabaseInstance(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "默认实例",
                "synthetic-sql-endpoint",
                Guid.NewGuid(),
                false,
                false,
                null,
                15));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "synthetic approved reason")]
    public void CertificateTrustConfigurationRequiresMatchingReason(
        bool trustServerCertificate,
        string? reason)
    {
        Assert.Throws<ArgumentException>(() =>
            new DatabaseInstance(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "默认实例",
                "synthetic-sql-endpoint",
                Guid.NewGuid(),
                true,
                trustServerCertificate,
                reason,
                15));
    }

    [Fact]
    public void ConnectionUpdateClearsPreviousProbeResult()
    {
        var instance = CreateInstance();
        instance.RecordConnectionSucceeded(
            new DateTimeOffset(2026, 9, 4, 6, 0, 0, TimeSpan.Zero),
            "10.50.6000.34",
            "SP3",
            "Synthetic Edition");

        instance.UpdateConnection(
            "new-synthetic-endpoint",
            Guid.NewGuid(),
            true,
            false,
            null,
            30);

        Assert.Equal(SqlConnectionStatus.Unknown, instance.ConnectionStatus);
        Assert.Null(instance.ProductVersion);
        Assert.Null(instance.LastConnectedAtUtc);
        Assert.Null(instance.LastConnectionCheckedAtUtc);
        Assert.Null(instance.LastConnectionErrorCode);
    }

    [Fact]
    public void ProbeTimestampsMustUseUtc()
    {
        var instance = CreateInstance();
        var nonUtc = new DateTimeOffset(2026, 9, 4, 14, 0, 0, TimeSpan.FromHours(8));

        Assert.Throws<ArgumentException>(() =>
            instance.RecordConnectionFailed(nonUtc, "synthetic_failure"));
    }

    [Fact]
    public void SystemDatabaseCannotBeManaged()
    {
        var database = new ManagedDatabase(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "synthetic-system-db",
            true,
            true,
            DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => database.SetManaged(true));
    }

    [Fact]
    public void DiscoveryChangingDatabaseToSystemRemovesManagementFlag()
    {
        var database = new ManagedDatabase(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "synthetic-business-db",
            false,
            true,
            DateTimeOffset.UtcNow);
        database.SetManaged(true);

        database.RefreshDiscovery(
            DateTimeOffset.UtcNow.AddMinutes(1),
            true,
            true,
            "SIMPLE",
            "ONLINE");

        Assert.False(database.IsManaged);
        Assert.True(database.IsSystemDatabase);
    }

    [Fact]
    public void SftpStorageTargetRequiresHostKeyFingerprint()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new StorageTarget(
                Guid.NewGuid(),
                "远程归档",
                new FileEndpointSettings(
                    FileTransferProtocol.Sftp,
                    "synthetic-sftp-host",
                    22,
                    "/synthetic/archive",
                    Guid.NewGuid(),
                    null)));
    }

    [Theory]
    [InlineData(BackupStorageMode.LocalOnly, true, false, false)]
    [InlineData(BackupStorageMode.LocalAndRemote, true, true, true)]
    [InlineData(BackupStorageMode.RemoteOnly, false, true, true)]
    public void BackupPolicyAcceptsStorageModeRequirements(
        BackupStorageMode storageMode,
        bool hasLocalRetention,
        bool hasRemoteRetention,
        bool hasStorageTarget)
    {
        var settings = CreatePolicySettings(
            storageMode,
            hasStorageTarget ? Guid.NewGuid() : null,
            hasLocalRetention ? 30 : null,
            hasRemoteRetention ? 90 : null);

        var policy = new BackupPolicy(
            Guid.NewGuid(),
            "每日备份",
            Guid.NewGuid(),
            settings);

        Assert.Equal(storageMode, policy.StorageMode);
        Assert.Equal(settings.StorageTargetId, policy.StorageTargetId);
        Assert.False(policy.IsEnabled);
    }

    [Theory]
    [InlineData(BackupStorageMode.LocalOnly, true, true, false)]
    [InlineData(BackupStorageMode.LocalOnly, true, false, true)]
    [InlineData(BackupStorageMode.LocalAndRemote, true, false, true)]
    [InlineData(BackupStorageMode.RemoteOnly, true, true, true)]
    [InlineData(BackupStorageMode.RemoteOnly, false, true, false)]
    public void BackupPolicyRejectsInvalidStorageModeCombinations(
        BackupStorageMode storageMode,
        bool hasLocalRetention,
        bool hasRemoteRetention,
        bool hasStorageTarget)
    {
        var settings = CreatePolicySettings(
            storageMode,
            hasStorageTarget ? Guid.NewGuid() : null,
            hasLocalRetention ? 30 : null,
            hasRemoteRetention ? 90 : null);

        Assert.ThrowsAny<ArgumentException>(() =>
            new BackupPolicy(
                Guid.NewGuid(),
                "无效策略",
                Guid.NewGuid(),
                settings));
    }

    [Fact]
    public void DailyPolicyRejectsWeekdays()
    {
        var settings = CreatePolicySettings(
            BackupStorageMode.LocalOnly,
            null,
            30,
            null) with
        {
            DaysOfWeek = BackupWeekdays.Monday
        };

        Assert.Throws<ArgumentException>(() =>
            new BackupPolicy(Guid.NewGuid(), "无效每日策略", Guid.NewGuid(), settings));
    }

    [Fact]
    public void WeeklyPolicyRequiresAtLeastOneWeekday()
    {
        var settings = CreatePolicySettings(
            BackupStorageMode.LocalOnly,
            null,
            30,
            null) with
        {
            ScheduleType = BackupScheduleType.Weekly,
            DaysOfWeek = BackupWeekdays.None
        };

        Assert.Throws<ArgumentException>(() =>
            new BackupPolicy(Guid.NewGuid(), "无效每周策略", Guid.NewGuid(), settings));
    }

    [Fact]
    public void WeeklyPolicyAcceptsMultipleWeekdays()
    {
        var settings = CreatePolicySettings(
            BackupStorageMode.LocalOnly,
            null,
            30,
            null) with
        {
            ScheduleType = BackupScheduleType.Weekly,
            DaysOfWeek = BackupWeekdays.Monday | BackupWeekdays.Friday
        };

        var policy = new BackupPolicy(Guid.NewGuid(), "每周策略", Guid.NewGuid(), settings);

        Assert.Equal(BackupWeekdays.Monday | BackupWeekdays.Friday, policy.DaysOfWeek);
    }

    [Fact]
    public void BackupPolicyRejectsUnknownTimeZone()
    {
        var settings = CreatePolicySettings(
            BackupStorageMode.LocalOnly,
            null,
            30,
            null) with
        {
            TimeZoneId = "Synthetic/Unknown-Time-Zone"
        };

        Assert.Throws<ArgumentException>(() =>
            new BackupPolicy(Guid.NewGuid(), "无效时区策略", Guid.NewGuid(), settings));
    }

    [Fact]
    public void InvalidPolicyUpdateDoesNotPartiallyChangeNameOrSettings()
    {
        var original = CreatePolicySettings(
            BackupStorageMode.LocalOnly,
            null,
            30,
            null);
        var policy = new BackupPolicy(Guid.NewGuid(), "原策略", Guid.NewGuid(), original);
        var invalid = original with
        {
            TimeZoneId = " ",
            UseChecksum = false
        };

        Assert.ThrowsAny<ArgumentException>(() => policy.Update("新策略", invalid));

        Assert.Equal("原策略", policy.Name);
        Assert.True(policy.UseChecksum);
        Assert.Equal("UTC", policy.TimeZoneId);
    }

    private static DatabaseServer CreateServer(FileEndpointSettings endpoint)
    {
        return new DatabaseServer(
            Guid.NewGuid(),
            "合成数据库服务器",
            "synthetic-sql-local-root",
            endpoint);
    }

    private static DatabaseInstance CreateInstance()
    {
        return new DatabaseInstance(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "默认实例",
            "synthetic-sql-endpoint",
            Guid.NewGuid(),
            true,
            false,
            null,
            15);
    }

    private static BackupPolicySettings CreatePolicySettings(
        BackupStorageMode storageMode,
        Guid? storageTargetId,
        int? localRetentionDays,
        int? remoteRetentionDays)
    {
        return new BackupPolicySettings(
            storageMode,
            storageTargetId,
            BackupScheduleType.Daily,
            new TimeOnly(2, 0),
            BackupWeekdays.None,
            "UTC",
            localRetentionDays,
            remoteRetentionDays,
            true,
            false,
            true,
            120,
            30,
            120);
    }
}
