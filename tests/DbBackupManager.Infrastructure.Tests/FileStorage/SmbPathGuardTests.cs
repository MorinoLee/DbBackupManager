using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class SmbPathGuardTests
{
    [Theory]
    [InlineData(@"\\synthetic-host\share")]
    [InlineData(@"\\synthetic-host\share\nested")]
    [InlineData(@"\\synthetic-host\share\nested\..\escape.bak")]
    [InlineData(@"\\synthetic-host\share-other\task.bak")]
    [InlineData(@"\\other-host\share\nested\task.bak")]
    [InlineData(@"\\?\UNC\synthetic-host\share\nested\task.bak")]
    [InlineData(@"\\synthetic-host\share\nested\task.bak\")]
    [InlineData(@"\\synthetic-host\share\nested\task.bak:stream")]
    public void ExactPathRejectsRootsTraversalPrefixCollisionAndAlternateStreams(string path)
    {
        var endpoint = Endpoint();

        var exception = Assert.Throws<FileStorageAdapterException>(() =>
            SmbPathGuard.ValidateExactFilePath(endpoint, path));

        Assert.Equal(BackupFileStorageFailureCode.PathRejected, exception.Code);
        Assert.Equal(nameof(BackupFileStorageFailureCode.PathRejected), exception.Message);
    }

    [Fact]
    public void ExactPathAllowsOnlyDescendantOfConfiguredUncRoot()
    {
        var path = @"\\SYNTHETIC-HOST\SHARE\NESTED\task.bak";

        var result = SmbPathGuard.ValidateExactFilePath(Endpoint(), path);

        Assert.Equal(path, result);
    }

    [Fact]
    public void ConnectionProbeFileUnderShareRootIsAllowed()
    {
        var endpoint = Endpoint();
        var path = endpoint.ChildFile("DbBackupManager.connection-probe");

        Assert.Equal(
            @"\\synthetic-host\share\nested\DbBackupManager.connection-probe",
            SmbPathGuard.ValidateExactFilePath(endpoint, path));
    }

    [Fact]
    public void RenameRequiresSameDirectory()
    {
        var endpoint = Endpoint();

        Assert.Throws<FileStorageAdapterException>(() => SmbPathGuard.EnsureSameDirectory(
            endpoint,
            @"\\synthetic-host\share\nested\task.part",
            @"\\synthetic-host\share\other\task.bak"));

        SmbPathGuard.EnsureSameDirectory(
            endpoint,
            @"\\synthetic-host\share\nested\task.part",
            @"\\synthetic-host\share\nested\task.bak");
    }

    [Fact]
    public void ParentDirectoriesStayUnderConfiguredRootAndAttributesRejectReparsePoints()
    {
        var endpoint = Endpoint();
        var path = @"\\synthetic-host\share\nested\server\instance\task.bak";

        Assert.Equal(
            [
                @"\\synthetic-host\share",
                @"\\synthetic-host\share\nested",
                @"\\synthetic-host\share\nested\server",
                @"\\synthetic-host\share\nested\server\instance",
            ],
            SmbPathGuard.ParentDirectoryPaths(endpoint, path));
        SmbPathGuard.ValidateDirectoryAttributes(
            FileAttributes.Directory,
            BackupFileStorageFailurePhase.DirectoryPrepare);
        Assert.Throws<FileStorageAdapterException>(() => SmbPathGuard.ValidateDirectoryAttributes(
            FileAttributes.Normal,
            BackupFileStorageFailurePhase.DirectoryPrepare));
        Assert.Throws<FileStorageAdapterException>(() => SmbPathGuard.ValidateDirectoryAttributes(
            FileAttributes.Directory | FileAttributes.ReparsePoint,
            BackupFileStorageFailurePhase.DirectoryPrepare));
    }

    private static BackupFileEndpointInput Endpoint() => new(
        FileTransferProtocol.Smb,
        "synthetic-host",
        null,
        "share\\nested",
        Guid.NewGuid(),
        null);
}
