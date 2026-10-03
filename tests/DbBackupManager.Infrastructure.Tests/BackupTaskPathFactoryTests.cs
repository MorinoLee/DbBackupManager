using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupTaskPathFactoryTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DatabaseId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AttemptId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset PreparedAtUtc = new(2026, 9, 22, 2, 30, 45, TimeSpan.Zero);

    [Fact]
    public void CurrentLayoutBuildsSameHierarchyForSqlSmbAndSftp()
    {
        var snapshot = CreateSnapshot(
            BackupTaskPathFactory.CurrentVersion,
            "DB/Prod",
            BackupStorageMode.LocalAndRemote,
            remoteProtocol: FileTransferProtocol.Sftp);

        var paths = BackupTaskPathFactory.Create(snapshot, AttemptId, PreparedAtUtc);
        const string relative = "Server_One--11111111\\MSSQLSERVER--22222222\\DB_Prod--33333333\\full\\20260922023045_44444444444444444444444444444444.bak";

        Assert.Equal($@"D:\SqlBackups\{relative}", paths.LocalSqlFilePath);
        Assert.Equal($@"\\source-host\SqlBackups$\{relative}", paths.WorkerSourceFilePath);
        Assert.Equal(
            "/archive/Server_One--11111111/MSSQLSERVER--22222222/DB_Prod--33333333/full/20260922023045_44444444444444444444444444444444.bak",
            paths.RemoteFinalFilePath);
        Assert.Equal($"{paths.RemoteFinalFilePath}.part", paths.RemotePartialFilePath);
    }

    [Fact]
    public void IdentitySegmentAlwaysIncludesStableIdAndAvoidsSanitizationCollision()
    {
        var first = BackupTaskPathFactory.BuildIdentitySegment("ERP/A", DatabaseId);
        var second = BackupTaskPathFactory.BuildIdentitySegment("ERP:A", Guid.Parse("33333334-3333-3333-3333-333333333333"));

        Assert.Equal("ERP_A--33333333", first);
        Assert.Equal("ERP_A--33333334", second);
        Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("..")]
    [InlineData("  ")]
    [InlineData("name. ")]
    public void IdentitySegmentIsSafeAndBounded(string value)
    {
        var segment = BackupTaskPathFactory.BuildIdentitySegment(value, DatabaseId);

        Assert.EndsWith("--33333333", segment, StringComparison.Ordinal);
        Assert.InRange(segment.Length, 11, BackupTaskPathFactory.MaximumSegmentLength);
        Assert.DoesNotContain('\\', segment);
        Assert.DoesNotContain('/', segment);
        Assert.False(segment.EndsWith('.'));
        Assert.False(segment.EndsWith(' '));
    }

    [Fact]
    public void CurrentLayoutRejectsSqlServerPathBeyondLegacyLimit()
    {
        var snapshot = CreateSnapshot(
            BackupTaskPathFactory.CurrentVersion,
            new string('D', 128),
            BackupStorageMode.LocalOnly,
            localRoot: $@"D:\{new string('R', 150)}");

        var error = Assert.Throws<InvalidOperationException>(
            () => BackupTaskPathFactory.Create(snapshot, AttemptId, PreparedAtUtc));

        Assert.Equal("生成的备份路径超过支持的长度。", error.Message);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v3")]
    public void UnsupportedLayoutVersionFailsClosed(string version)
    {
        var snapshot = CreateSnapshot(version, "Sales", BackupStorageMode.LocalOnly);

        var error = Assert.Throws<InvalidOperationException>(
            () => BackupTaskPathFactory.Create(snapshot, AttemptId, PreparedAtUtc));

        Assert.Equal("任务快照包含不支持的备份路径规则版本。", error.Message);
    }

    [Fact]
    public void PartialFileSuffixMustFitWithinStoredPathLimit()
    {
        var baseline = CreateSnapshot("v2", "Sales", BackupStorageMode.LocalAndRemote, FileTransferProtocol.Sftp);
        var paths = BackupTaskPathFactory.Create(baseline, AttemptId, PreparedAtUtc);
        var rootLength = BackupTaskPathFactory.MaximumStoredPathLength - paths.RemoteFinalFilePath!.Length + "/archive".Length;
        var snapshot = CreateSnapshot("v2", "Sales", BackupStorageMode.LocalAndRemote, FileTransferProtocol.Sftp,
            remoteRoot: "/" + new string('r', rootLength - 1));

        Assert.False(BackupTaskPathFactory.TryCreate(snapshot, AttemptId, PreparedAtUtc, out var rejected));
        Assert.Null(rejected);
    }

    private static BackupTaskSnapshot CreateSnapshot(
        string version,
        string databaseName,
        BackupStorageMode storageMode,
        FileTransferProtocol remoteProtocol = FileTransferProtocol.Smb,
        string localRoot = @"D:\SqlBackups",
        string? remoteRoot = null)
    {
        BackupRemoteTargetSnapshot? remote = storageMode == BackupStorageMode.LocalOnly
            ? null
            : new(
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                Endpoint(remoteProtocol, remoteProtocol == FileTransferProtocol.Smb ? "target-host" : "sftp-host",
                    remoteRoot ?? (remoteProtocol == FileTransferProtocol.Smb ? "Archive" : "/archive")));

        return new BackupTaskSnapshot(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            "Synthetic policy",
            new BackupTaskIdentitySnapshot(
                ServerId,
                "Server/One",
                InstanceId,
                "MSSQLSERVER",
                DatabaseId,
                databaseName),
            new BackupSqlTargetSnapshot(
                "synthetic-sql,1433",
                Guid.Parse("77777777-7777-7777-7777-777777777777"),
                true,
                false,
                null,
                15),
            new BackupSourceSnapshot(
                localRoot,
                version,
                Endpoint(FileTransferProtocol.Smb, "source-host", "SqlBackups$")),
            new BackupTaskPolicySnapshot(
                storageMode,
                remote,
                storageMode == BackupStorageMode.RemoteOnly ? null : 7,
                storageMode == BackupStorageMode.LocalOnly ? null : 30,
                true,
                false,
                true,
                60,
                30,
                30,
                "UTC"));
    }

    private static FileEndpointSettings Endpoint(
        FileTransferProtocol protocol,
        string host,
        string path) => new(
            protocol,
            host,
            protocol == FileTransferProtocol.Sftp ? 22 : null,
            path,
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            protocol == FileTransferProtocol.Sftp ? "SHA256:synthetic" : null);
}
