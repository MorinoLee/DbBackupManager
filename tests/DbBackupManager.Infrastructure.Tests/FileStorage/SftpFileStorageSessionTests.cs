using System.Reflection;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class SftpFileStorageSessionTests
{
    private const string Root = "/archive/tenant";
    private const string Parent = Root + "/daily";
    private const string FilePath = Parent + "/task.part";

    [Theory]
    [InlineData("/archive")]
    [InlineData(Root)]
    public async Task PreparationDoesNotCreateMissingRootOrAncestors(string missingPath)
    {
        var client = ScriptedClient.Create();
        client.Read = path => path == missingPath ? Missing() : Attributes(0x4000);
        await using var session = Session(client);

        var exception = await Assert.ThrowsAsync<FileStorageAdapterException>(() =>
            session.PrepareParentDirectoryAsync(FilePath, default).AsTask());

        Assert.Equal(BackupFileStorageFailureCode.FileNotFound, exception.Code);
        Assert.Equal(BackupFileStorageFailurePhase.DirectoryPrepare, exception.Phase);
        Assert.Empty(client.Created);
    }

    [Theory]
    [InlineData(0x8000u)]
    [InlineData(0xA000u)]
    public async Task UnsafeParentStopsPreparationBeforeAnyCreation(uint type)
    {
        var client = ScriptedClient.Create();
        client.Read = path => Attributes(path == Root ? type : 0x4000);
        await using var session = Session(client);

        var exception = await Assert.ThrowsAsync<FileStorageAdapterException>(() =>
            session.PrepareParentDirectoryAsync(FilePath, default).AsTask());

        Assert.Equal(BackupFileStorageFailureCode.PathRejected, exception.Code);
        Assert.Empty(client.Created);
        Assert.DoesNotContain(Parent, client.Reads);
    }

    [Theory]
    [InlineData(0x4000u, true, false)]
    [InlineData(0x4000u, true, true)]
    [InlineData(0x8000u, false, true)]
    [InlineData(0xA000u, false, true)]
    public async Task CreationIsAcceptedOnlyAfterSafeDirectoryReinspection(uint type, bool safe, bool concurrent)
    {
        var client = ScriptedClient.Create();
        client.Read = path => path != Parent
            ? Attributes(0x4000)
            : client.Created.Count == 0 ? Missing() : Attributes(type);
        if (concurrent)
        {
            client.CreateDirectory = _ => throw new SshException("synthetic concurrent creation");
        }
        await using var session = Session(client);

        if (safe)
        {
            await session.PrepareParentDirectoryAsync(FilePath, default);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<FileStorageAdapterException>(() =>
                session.PrepareParentDirectoryAsync(FilePath, default).AsTask());
            Assert.Equal(BackupFileStorageFailureCode.PathRejected, exception.Code);
        }

        Assert.Equal([Parent], client.Created);
        Assert.Equal(2, client.Reads.Count(path => path == Parent));
    }

    [Fact]
    public async Task CancellationDuringConcurrentCreationReinspectionIsPreserved()
    {
        var client = ScriptedClient.Create();
        client.Read = path => path != Parent
            ? Attributes(0x4000)
            : client.Created.Count == 0 ? Missing() : throw new OperationCanceledException();
        client.CreateDirectory = _ => throw new SshException("synthetic creation failure");
        await using var session = Session(client);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.PrepareParentDirectoryAsync(FilePath, default).AsTask());
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("read")]
    [InlineData("create")]
    [InlineData("delete")]
    public async Task FileOperationsNeverCreateMissingParents(string operation)
    {
        var client = ScriptedClient.Create();
        client.Read = path => path == Parent ? Missing() : Attributes(0x4000);
        await using var session = Session(client);

        var exception = await Assert.ThrowsAsync<FileStorageAdapterException>(async () =>
        {
            switch (operation)
            {
                case "inspect": await session.InspectAsync(FilePath, default); break;
                case "read": await session.OpenReadAsync(FilePath, default); break;
                case "create": await session.CreateNewAsync(FilePath, default); break;
                case "delete": await session.DeleteAsync(FilePath, default); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
        });

        Assert.Equal(BackupFileStorageFailureCode.PathRejected, exception.Code);
        Assert.Equal(BackupFileStorageFailurePhase.RequestValidation, exception.Phase);
        Assert.Empty(client.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameChecksSharedParentsOnceAndRejectsExistingDestination(bool destinationExists)
    {
        var client = ScriptedClient.Create();
        client.Read = path => path.EndsWith(".bak", StringComparison.Ordinal)
            ? destinationExists ? Attributes(0x8000) : Missing()
            : Attributes(0x4000);
        await using var session = Session(client);

        if (destinationExists)
        {
            var exception = await Assert.ThrowsAsync<FileStorageAdapterException>(() =>
                session.RenameNoReplaceAsync(FilePath, Parent + "/task.bak", default).AsTask());
            Assert.Equal(BackupFileStorageFailureCode.FileAlreadyExists, exception.Code);
            Assert.Empty(client.Mutations);
        }
        else
        {
            await session.RenameNoReplaceAsync(FilePath, Parent + "/task.bak", default);
            Assert.Equal([nameof(ISftpClient.RenameFileAsync)], client.Mutations);
        }

        Assert.Equal(["/archive", Root, Parent, Parent + "/task.bak"], client.Reads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MutationDisconnectOrTimeoutRemainsIndeterminate(bool delete, bool timeout)
    {
        var client = ScriptedClient.Create();
        client.Read = path => path.EndsWith(".part", StringComparison.Ordinal)
            ? Attributes(0x8000) : path.EndsWith(".bak", StringComparison.Ordinal)
                ? Missing() : Attributes(0x4000);
        client.Mutation = () => throw (timeout
            ? new SshOperationTimeoutException("synthetic timeout")
            : new SshConnectionException("synthetic disconnect"));
        await using var session = Session(client);

        var exception = await Assert.ThrowsAsync<FileStorageAdapterException>(() => delete
            ? session.DeleteAsync(FilePath, default).AsTask()
            : session.RenameNoReplaceAsync(FilePath, Parent + "/task.bak", default).AsTask());

        Assert.True(exception.Indeterminate);
        Assert.Equal(timeout ? BackupFileStorageFailureCode.TimedOut
            : BackupFileStorageFailureCode.ConnectionInterrupted, exception.Code);
        Assert.Equal(delete ? BackupFileStorageFailurePhase.Delete
            : BackupFileStorageFailurePhase.Rename, exception.Phase);
    }

    private static SftpFileStorageSession Session(ScriptedClient client)
    {
        using var credential = FileStorageCredentialLease.CreateAndClear(
            CredentialKind.SftpPassword, "synthetic-user", "synthetic-password".ToCharArray(), null);
        return new SftpFileStorageSession(
            new BackupFileEndpointInput(FileTransferProtocol.Sftp, "synthetic-host", 22,
                Root, Guid.NewGuid(), $"SHA256:{Convert.ToBase64String(new byte[32]).TrimEnd('=')}"),
            (ISftpClient)client,
            SftpConnectionResources.Create(credential));
    }

    private static SftpFileAttributes Missing() => throw new SftpPathNotFoundException("synthetic missing path");

    private static SftpFileAttributes Attributes(uint type) =>
        // SSH.NET 的属性由协议响应创建；受控客户端只模拟协议返回，不连接远端。
        (SftpFileAttributes)Activator.CreateInstance(typeof(SftpFileAttributes),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [DateTime.UnixEpoch, DateTime.UnixEpoch, 10L, 0, 0, type, null], null)!;

    public class ScriptedClient : DispatchProxy
    {
        public Func<string, SftpFileAttributes> Read { get; set; } = _ => throw new NotSupportedException();
        public Action<string> CreateDirectory { get; set; } = _ => { };
        public Action Mutation { get; set; } = () => { };
        public List<string> Reads { get; } = [];
        public List<string> Created { get; } = [];
        public List<string> Mutations { get; } = [];

        public static ScriptedClient Create() => (ScriptedClient)Create<ISftpClient, ScriptedClient>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(ISftpClient.GetAttributesAsync):
                    var path = (string)args![0]!;
                    Reads.Add(path);
                    return Task.FromResult(Read(path));
                case nameof(ISftpClient.CreateDirectoryAsync):
                    var directory = (string)args![0]!;
                    Created.Add(directory);
                    CreateDirectory(directory);
                    return Task.CompletedTask;
                case nameof(ISftpClient.RenameFileAsync):
                case nameof(ISftpClient.DeleteFileAsync):
                    Mutations.Add(targetMethod.Name);
                    Mutation();
                    return Task.CompletedTask;
                case nameof(IDisposable.Dispose): return null;
                default: throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
