using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests.FileStorage;

public sealed class BackupFileStorageContractTests
{
    private static readonly string[] ForbiddenPropertyFragments =
    [
        "Password",
        "Secret",
        "PrivateKey",
        "Protected",
        "ConnectionString",
        "UserName",
    ];

    [Fact]
    public void ReadTransferAndDeleteCapabilitiesRemainSeparate()
    {
        Assert.Equal(["InspectAsync"], MethodNames<IBackupFileStorageProbe>());
        Assert.Equal(["PrepareParentAsync"], MethodNames<IBackupDirectoryPreparer>());
        Assert.Equal(["RenameAsync", "TransferAsync"], MethodNames<IBackupFileTransferExecutor>());
        Assert.Equal(["DeleteAsync"], MethodNames<IBackupFileDeletionExecutor>());
    }

    [Fact]
    public void ContractsContainCredentialReferencesButNeverSecrets()
    {
        var contractTypes = typeof(BackupFileEndpointInput).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(BackupFileEndpointInput).Namespace)
            .ToArray();
        var propertyNames = contractTypes
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(BackupFileEndpointInput.CredentialReferenceId), propertyNames);
        Assert.DoesNotContain(propertyNames, name => ForbiddenPropertyFragments.Any(
            fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SmbEndpointRejectsUnsafeHostRootAndSftpFields()
    {
        var credentialId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => Smb("host\\escape", "share", credentialId));
        Assert.Throws<ArgumentException>(() => Smb("host", "share\\..\\escape", credentialId));
        Assert.Throws<ArgumentException>(() => Smb("host", "share. ", credentialId));
        Assert.Throws<ArgumentException>(() => new BackupFileEndpointInput(
            FileTransferProtocol.Smb,
            "host",
            445,
            "share",
            credentialId,
            null));
        Assert.Throws<ArgumentException>(() => new BackupFileEndpointInput(
            FileTransferProtocol.Smb,
            "host",
            null,
            "share",
            credentialId,
            Fingerprint));

        var endpoint = Smb(" synthetic-host ", "share\\nested", credentialId);
        Assert.Equal("synthetic-host", endpoint.Host);
        Assert.Equal("share\\nested", endpoint.RootPath);
    }

    [Fact]
    public void ChildFilePathJoinsSingleSegmentUnderConfiguredRoot()
    {
        var smb = Smb("synthetic-host", "share\\nested", Guid.NewGuid());
        Assert.Equal(
            @"\\synthetic-host\share\nested\DbBackupManager.connection-probe",
            smb.ChildFile("DbBackupManager.connection-probe"));
        Assert.Throws<ArgumentException>(() => smb.ChildFile("nested\\escape.bak"));

        var sftp = Sftp("synthetic-host", 22, "/archive/tenant", Guid.NewGuid(), Fingerprint);
        Assert.Equal(
            "/archive/tenant/DbBackupManager.connection-probe",
            sftp.ChildFile("DbBackupManager.connection-probe"));
        Assert.Throws<ArgumentException>(() => sftp.ChildFile("../escape.bak"));
    }

    [Fact]
    public void ChildPathJoinsValidatedHierarchyUnderConfiguredRoot()
    {
        var segments = new[] { "server--11111111", "instance--22222222", "database--33333333", "full", "backup.bak" };

        Assert.Equal(
            @"\\server-a\share\root\server--11111111\instance--22222222\database--33333333\full\backup.bak",
            BackupFileEndpointInput.CombineChildPath(
                FileTransferProtocol.Smb,
                "server-a",
                @"share\root",
                segments));
        Assert.Equal(
            "/archive/server--11111111/instance--22222222/database--33333333/full/backup.bak",
            BackupFileEndpointInput.CombineChildPath(
                FileTransferProtocol.Sftp,
                "sftp-a",
                "/archive",
                segments));
        Assert.Throws<ArgumentException>(() => BackupFileEndpointInput.CombineChildPath(
            FileTransferProtocol.Smb,
            "server-a",
            "share",
            ["full", "..", "backup.bak"]));
        Assert.Throws<ArgumentException>(() => BackupFileEndpointInput.CombineChildPath(
            FileTransferProtocol.Sftp,
            "sftp-a",
            "/archive",
            []));
    }

    [Fact]
    public void SftpEndpointRequiresNormalizedAbsoluteRootAndSha256HostKey()
    {
        var credentialId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => Sftp("host", 22, "relative", credentialId, Fingerprint));
        Assert.Throws<ArgumentException>(() => Sftp("host", 22, "/", credentialId, Fingerprint));
        Assert.Throws<ArgumentException>(() => Sftp("host", 22, "/safe/../escape", credentialId, Fingerprint));
        Assert.Throws<ArgumentException>(() => Sftp("host", 22, "/safe", credentialId, "MD5:00:11"));
        Assert.Throws<ArgumentException>(() => Sftp("host", 22, "/safe", credentialId, "SHA256:bad"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sftp("host", null, "/safe", credentialId, Fingerprint));

        var endpoint = Sftp(" synthetic-host ", 22, "/safe/nested", credentialId, $"{Fingerprint}=");
        Assert.Equal("synthetic-host", endpoint.Host);
        Assert.Equal("/safe/nested", endpoint.RootPath);
        Assert.Equal(Fingerprint, endpoint.SftpHostKeyFingerprint);
    }

    [Fact]
    public void MutationRequestsRequireTaskPathsLengthsAndTimeouts()
    {
        var endpoint = Smb("synthetic-host", "share", Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => new BackupFileTransferRequest(
            endpoint,
            "source.bak",
            endpoint,
            "target.bak",
            1,
            30));
        Assert.Throws<ArgumentException>(() => new BackupFileRenameRequest(
            endpoint,
            "target.part",
            "target.tmp",
            1,
            30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupFileDeleteRequest(
            endpoint,
            "target.bak",
            0,
            null,
            30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupFileTransferRequest(
            endpoint,
            "source.bak",
            endpoint,
            "target.part",
            1,
            0));
    }

    [Fact]
    public void MetadataAndResultsCannotRepresentContradictoryFacts()
    {
        Assert.Throws<ArgumentException>(() => new BackupFileMetadata(
            exists: false,
            isRegularFile: true,
            lengthBytes: 1));
        Assert.Throws<ArgumentException>(() => new BackupFileMetadata(
            exists: true,
            isRegularFile: true,
            lengthBytes: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupFileMetadata(
            exists: true,
            isRegularFile: true,
            lengthBytes: -1));

        var confirmed = BackupFileStorageResult.ConfirmedFailure<BackupFileMetadata>(
            BackupFileStorageFailureCode.AuthorizationDenied,
            BackupFileStorageFailurePhase.SourceInspection);
        var uncertain = BackupFileStorageResult.Indeterminate<BackupFileMutationReceipt>(
            BackupFileStorageFailureCode.ConnectionInterrupted,
            BackupFileStorageFailurePhase.Rename);

        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, confirmed.Outcome);
        Assert.Equal(BackupFileStorageFailureCode.AuthorizationDenied, confirmed.Failure!.Code);
        Assert.Equal(BackupFileStorageOutcome.Indeterminate, uncertain.Outcome);
        Assert.Equal(BackupFileStorageFailurePhase.Rename, uncertain.Failure!.Phase);
    }

    private const string Fingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static string[] MethodNames<T>() => typeof(T).GetMethods()
        .Select(method => method.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static BackupFileEndpointInput Smb(string host, string root, Guid credentialId) =>
        new(FileTransferProtocol.Smb, host, null, root, credentialId, null);

    private static BackupFileEndpointInput Sftp(
        string host,
        int? port,
        string root,
        Guid credentialId,
        string fingerprint) =>
        new(FileTransferProtocol.Sftp, host, port, root, credentialId, fingerprint);
}
