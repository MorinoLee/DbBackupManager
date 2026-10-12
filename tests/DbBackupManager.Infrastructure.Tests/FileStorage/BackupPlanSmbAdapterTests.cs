using System.Security.Principal;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class BackupPlanSmbAdapterTests
{
    [Fact]
    public async Task RealLoopbackSmbHashesTransfersAndRenamesUnderObjectProtection()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"DbBackupManagerSmbPlan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var bytes = Enumerable.Range(0, 65537).Select(x => (byte)(x % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(root, "source.bak"), bytes);
        var endpoint = new BackupFileEndpointInput(FileTransferProtocol.Smb, "localhost", null,
            $"{root[0]}$\\{root[3..]}", Guid.NewGuid(), null);
        var source = endpoint.ChildFile("source.bak");
        var part = endpoint.ChildFile("copy.bak.part");
        var final = endpoint.ChildFile("copy.bak");
        var adapter = new BackupPlanFileAdapter([new CurrentIdentitySmbFactory()]);
        var task = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        try
        {
            var verified = await adapter.OpenVerifiedAsync(new(task, attempt, endpoint, source, 30));
            Assert.True(verified.IsSucceeded, verified.Failure?.Code.ToString());
            await using var sourceHandle = verified.Value!;
            var capability = await adapter.CheckCapabilitiesAsync(new(task, attempt, endpoint, part, 30));
            Assert.True(capability.IsSucceeded, capability.Failure?.Code.ToString());
            var transferred = await adapter.TransferVerifiedAsync(Guid.NewGuid(), sourceHandle, new(task, attempt, endpoint, part, 30));
            Assert.True(transferred.IsSucceeded, transferred.Failure?.Code.ToString());
            Assert.True(transferred.Value!.MatchesVerifiedSource(sourceHandle.Content));
            var confirmed = await adapter.RenameAndVerifyAsync(Guid.NewGuid(),
                new(task, attempt, endpoint, part, 30, transferred.Value.PartialReadBackContent), final);
            Assert.True(confirmed.IsSucceeded, confirmed.Failure?.Code.ToString());
            await using var targetHandle = confirmed.Value!.FinalHandle;
            Assert.True(confirmed.Value.CanRegisterAvailable(sourceHandle.Content, endpoint, final));
            Assert.False(File.Exists(part));
            await using (var readable = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous))
            {
                var received = new byte[bytes.Length];
                await readable.ReadExactlyAsync(received);
                Assert.Equal(bytes, received);
            }
            Assert.Throws<IOException>(() => File.WriteAllBytes(final, bytes));
            Assert.Throws<IOException>(() => File.Delete(final));
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-replaced"));
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), absolute, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("DbBackupManagerSmbPlan_", Path.GetFileName(absolute), StringComparison.Ordinal);
            Directory.Delete(absolute, recursive: true);
        }
    }

    [Theory]
    [InlineData("file.bak")]
    [InlineData("")]
    [InlineData(@"\file.bak")]
    public void CapabilityProbeWithoutParentDirectoryDoesNotSlicePastTheStart(string path) =>
        Assert.False(BackupPlanFileAdapter.TryGetCapabilityParent(path, out _));

    [Fact]
    public void CapabilityProbeParentStopsAtTheLastBackslash()
    {
        Assert.True(BackupPlanFileAdapter.TryGetCapabilityParent(@"source\dir\file.bak", out var parent));
        Assert.Equal(@"source\dir", parent);
    }

    [Fact]
    public async Task SmbCapabilityPathWithoutParentIsRejectedBeforeAnyProbe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var endpoint = new BackupFileEndpointInput(FileTransferProtocol.Smb, "localhost", null, "synthetic", Guid.NewGuid(), null);
        var result = await new BackupPlanFileAdapter([]).CheckCapabilitiesAsync(new(Guid.NewGuid(), Guid.NewGuid(), endpoint, "file.bak", 30));
        Assert.Equal(BackupFileStorageFailureCode.PathRejected, result.Failure!.Code);
        Assert.Equal(BackupFileStorageFailurePhase.SourceInspection, result.Failure.Phase);
    }

    [Fact]
    public async Task SftpIsRejectedBeforeProtocolConnectionWithoutProvenProtection()
    {
        var factory = new CurrentIdentitySmbFactory();
        var adapter = new BackupPlanFileAdapter([factory]);
        var endpoint = new BackupFileEndpointInput(FileTransferProtocol.Sftp, "synthetic-sftp", 22, "/synthetic",
            Guid.NewGuid(), "SHA256:" + Convert.ToBase64String(new byte[32]));
        var result = await adapter.CheckCapabilitiesAsync(new(Guid.NewGuid(), Guid.NewGuid(), endpoint, "/synthetic/test.part", 30));
        Assert.False(result.IsSucceeded);
        Assert.Equal(BackupFileStorageFailureCode.PlatformUnsupported, result.Failure!.Code);
        Assert.Equal(0, factory.Opens);
    }

    internal sealed class CurrentIdentitySmbFactory : IFileStorageProtocolSessionFactory
    {
        public int Opens { get; private set; }
        public FileTransferProtocol Protocol => FileTransferProtocol.Smb;
        public ValueTask<IFileStorageProtocolSession> OpenAsync(BackupFileEndpointInput endpoint, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            Opens++;
            var identity = WindowsIdentity.GetCurrent();
            return ValueTask.FromResult<IFileStorageProtocolSession>(new OwnedIdentitySession(identity,
                new SmbFileStorageSession(endpoint, identity.AccessToken)));
        }
    }
    private sealed class OwnedIdentitySession(WindowsIdentity identity, SmbFileStorageSession session)
        : IFileStorageProtocolSession, IProtectedPlanFileSession
    {
        public NativePlanFileGuard OpenGuarded(string path, bool create, bool rename)
        { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return session.OpenGuarded(path, create, rename); }
        public void RenameGuarded(NativePlanFileGuard file, string finalPath)
        { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); session.RenameGuarded(file, finalPath); }
        public ValueTask PrepareParentDirectoryAsync(string path, CancellationToken token)
        { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return session.PrepareParentDirectoryAsync(path, token); }
        public ValueTask<BackupFileMetadata> InspectAsync(string path, CancellationToken token)
        { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return session.InspectAsync(path, token); }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return session.OpenReadAsync(path, token); }
        public ValueTask<Stream> CreateNewAsync(string path, CancellationToken token)
        { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return session.CreateNewAsync(path, token); }
        public ValueTask RenameNoReplaceAsync(string from, string to, CancellationToken token)
        { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return session.RenameNoReplaceAsync(from, to, token); }
        public ValueTask DeleteAsync(string path, CancellationToken token)
        { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); return session.DeleteAsync(path, token); }
        public async ValueTask DisposeAsync()
        {
            if (!OperatingSystem.IsWindows()) return;
            try { await session.DisposeAsync(); }
            finally { identity.Dispose(); }
        }

        public void VerifyGuardedCapabilities(NativePlanFileGuard file)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            session.VerifyGuardedCapabilities(file);
        }
    }
}
