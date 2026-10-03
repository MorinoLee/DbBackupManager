using System.Security.Cryptography;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class SftpPathGuardTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/archive/tenant")]
    [InlineData("/archive/tenant/")]
    [InlineData("/archive/tenant/../escape.bak")]
    [InlineData("/archive/tenant-other/task.bak")]
    [InlineData("/Archive/tenant/task.bak")]
    [InlineData("/archive//tenant/task.bak")]
    [InlineData("/archive/tenant/task.bak/")]
    [InlineData("/archive/tenant\\task.bak")]
    public void ExactPathRejectsRootTraversalPrefixCollisionAndWrongSeparators(string path)
    {
        var exception = Assert.Throws<FileStorageAdapterException>(() =>
            SftpPathGuard.ValidateExactFilePath(Endpoint(), path));

        Assert.Equal(BackupFileStorageFailureCode.PathRejected, exception.Code);
    }

    [Fact]
    public void ExactPathUsesCaseSensitiveSftpSegmentsAndEnumeratesEveryParent()
    {
        var path = "/archive/tenant/daily/task.bak";

        var result = SftpPathGuard.ValidateExactFilePath(Endpoint(), path);

        Assert.Equal(path, result);
        Assert.Equal(
            ["/archive", "/archive/tenant", "/archive/tenant/daily"],
            SftpPathGuard.ParentPaths(result));
        Assert.False(SftpPathGuard.IsDescendantOfRoot(Endpoint(), "/archive"));
        Assert.False(SftpPathGuard.IsDescendantOfRoot(Endpoint(), "/archive/tenant"));
        Assert.True(SftpPathGuard.IsDescendantOfRoot(Endpoint(), "/archive/tenant/daily"));
        Assert.False(SftpPathGuard.IsDescendantOfRoot(Endpoint(), "/archive/tenant-other"));
    }

    [Fact]
    public void ConnectionProbeFileUnderAbsoluteRootIsAllowed()
    {
        var endpoint = Endpoint();
        var path = endpoint.ChildFile("DbBackupManager.connection-probe");

        Assert.Equal(
            "/archive/tenant/DbBackupManager.connection-probe",
            SftpPathGuard.ValidateExactFilePath(endpoint, path));
    }

    [Fact]
    public void RenameRequiresSameCaseSensitiveDirectory()
    {
        var endpoint = Endpoint();

        Assert.Throws<FileStorageAdapterException>(() => SftpPathGuard.EnsureSameDirectory(
            endpoint,
            "/archive/tenant/daily/task.part",
            "/archive/tenant/other/task.bak"));
        Assert.Throws<FileStorageAdapterException>(() => SftpPathGuard.EnsureSameDirectory(
            endpoint,
            "/archive/tenant/daily/task.part",
            "/archive/tenant/Daily/task.bak"));

        SftpPathGuard.EnsureSameDirectory(
            endpoint,
            "/archive/tenant/daily/task.part",
            "/archive/tenant/daily/task.bak");
    }

    [Fact]
    public void HostKeyVerifierAcceptsOnlyExactSha256Fingerprint()
    {
        var key = "synthetic-host-key"u8.ToArray();
        var hash = SHA256.HashData(key);
        var fingerprint = $"SHA256:{Convert.ToBase64String(hash).TrimEnd('=')}";

        Assert.True(SftpHostKeyVerifier.Matches(fingerprint, key));
        Assert.False(SftpHostKeyVerifier.Matches(fingerprint, "other-host-key"u8));
        Assert.False(SftpHostKeyVerifier.Matches("SHA256:not-valid", key));
        Assert.False(SftpHostKeyVerifier.Matches(
            fingerprint.ToLowerInvariant(),
            key));
    }

    private static BackupFileEndpointInput Endpoint() => new(
        FileTransferProtocol.Sftp,
        "synthetic-host",
        22,
        "/archive/tenant",
        Guid.NewGuid(),
        $"SHA256:{Convert.ToBase64String(new byte[32]).TrimEnd('=')}");
}
