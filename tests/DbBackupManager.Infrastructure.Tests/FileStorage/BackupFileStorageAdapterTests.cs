using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class BackupFileStorageAdapterTests
{
    [Fact]
    public async Task DirectoryPreparationUsesExactEndpointAndIsIdempotent()
    {
        var endpoint = Smb("target-host", "target-share");
        var factory = new MemorySessionFactory();
        var adapter = new BackupFileStorageAdapter([factory]);
        var request = new BackupDirectoryPreparationRequest(endpoint, PartialPath, 30);

        var first = await adapter.PrepareParentAsync(request);
        var second = await adapter.PrepareParentAsync(request);

        Assert.True(first.IsSucceeded);
        Assert.True(second.IsSucceeded);
        Assert.Equal([PartialPath, PartialPath], factory.PreparedPaths);
        Assert.Equal(0, factory.CreateNewCount);
    }

    [Fact]
    public async Task DirectoryPreparationFailureRemainsConfirmedBecauseItCanBeReplayed()
    {
        var endpoint = Smb("target-host", "target-share");
        var factory = new MemorySessionFactory { FailDirectoryPreparation = true };
        var adapter = new BackupFileStorageAdapter([factory]);

        var result = await adapter.PrepareParentAsync(
            new BackupDirectoryPreparationRequest(endpoint, PartialPath, 30));

        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(BackupFileStorageFailureCode.ConnectionInterrupted, result.Failure!.Code);
        Assert.Equal(BackupFileStorageFailurePhase.DirectoryPrepare, result.Failure.Phase);
    }

    [Fact]
    public async Task TransferStreamsToExclusivePartialAndVerifiesBothSides()
    {
        var source = Smb("source-host", "source-share");
        var target = Smb("target-host", "target-share");
        var factory = new MemorySessionFactory();
        factory.Put(source, SourcePath, [1, 2, 3, 4], "source-id");
        var adapter = new BackupFileStorageAdapter([factory]);

        var result = await adapter.TransferAsync(new BackupFileTransferRequest(
            source,
            SourcePath,
            target,
            PartialPath,
            expectedLengthBytes: 4,
            timeoutSeconds: 30));

        Assert.True(result.IsSucceeded);
        Assert.Equal(4, result.Value!.LengthBytes);
        Assert.Equal([1, 2, 3, 4], factory.Read(target, PartialPath));
        Assert.Equal(1, factory.CreateNewCount);
    }

    [Fact]
    public async Task ExistingPartialFailsWithoutOpeningAWriterOrOverwriting()
    {
        var source = Smb("source-host", "source-share");
        var target = Smb("target-host", "target-share");
        var factory = new MemorySessionFactory();
        factory.Put(source, SourcePath, [1, 2, 3, 4]);
        factory.Put(target, PartialPath, [9]);
        var adapter = new BackupFileStorageAdapter([factory]);

        var result = await adapter.TransferAsync(new BackupFileTransferRequest(
            source,
            SourcePath,
            target,
            PartialPath,
            expectedLengthBytes: 4,
            timeoutSeconds: 30));

        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(BackupFileStorageFailureCode.FileAlreadyExists, result.Failure!.Code);
        Assert.Equal([9], factory.Read(target, PartialPath));
        Assert.Equal(0, factory.CreateNewCount);
    }

    [Fact]
    public async Task WriteFailureAfterExclusiveCreateIsIndeterminateAndKeepsPartial()
    {
        var source = Smb("source-host", "source-share");
        var target = Smb("target-host", "target-share");
        var factory = new MemorySessionFactory { FailWrites = true };
        factory.Put(source, SourcePath, [1, 2, 3, 4]);
        var adapter = new BackupFileStorageAdapter([factory]);

        var result = await adapter.TransferAsync(new BackupFileTransferRequest(
            source,
            SourcePath,
            target,
            PartialPath,
            expectedLengthBytes: 4,
            timeoutSeconds: 30));

        Assert.Equal(BackupFileStorageOutcome.Indeterminate, result.Outcome);
        Assert.Equal(BackupFileStorageFailureCode.ConnectionInterrupted, result.Failure!.Code);
        Assert.True(factory.Exists(target, PartialPath));
    }

    [Fact]
    public async Task RenameNeverOverwritesAndUncertainCallIsNotReportedAsFailure()
    {
        var endpoint = Smb("target-host", "target-share");
        var factory = new MemorySessionFactory { CompleteRenameThenFail = true };
        factory.Put(endpoint, PartialPath, [1, 2, 3, 4]);
        var adapter = new BackupFileStorageAdapter([factory]);

        var result = await adapter.RenameAsync(new BackupFileRenameRequest(
            endpoint,
            PartialPath,
            FinalPath,
            expectedLengthBytes: 4,
            timeoutSeconds: 30));

        Assert.Equal(BackupFileStorageOutcome.Indeterminate, result.Outcome);
        Assert.Equal(BackupFileStorageFailureCode.ConnectionInterrupted, result.Failure!.Code);
        Assert.False(factory.Exists(endpoint, PartialPath));
        Assert.True(factory.Exists(endpoint, FinalPath));
    }

    [Fact]
    public async Task DeleteRequiresMatchingLengthAndIdentityThenConfirmsAbsence()
    {
        var endpoint = Smb("target-host", "target-share");
        var factory = new MemorySessionFactory();
        factory.Put(endpoint, FinalPath, [1, 2, 3, 4], "file-id");
        var adapter = new BackupFileStorageAdapter([factory]);

        var stale = await adapter.DeleteAsync(new BackupFileDeleteRequest(
            endpoint,
            FinalPath,
            expectedLengthBytes: 4,
            expectedIdentity: "different-id",
            timeoutSeconds: 30));
        var deleted = await adapter.DeleteAsync(new BackupFileDeleteRequest(
            endpoint,
            FinalPath,
            expectedLengthBytes: 4,
            expectedIdentity: "file-id",
            timeoutSeconds: 30));

        Assert.Equal(BackupFileStorageFailureCode.SourceChanged, stale.Failure!.Code);
        Assert.True(deleted.IsSucceeded);
        Assert.False(factory.Exists(endpoint, FinalPath));
    }

    [Fact]
    public async Task SmbToSftpTransferUsesExclusivePartialAndMatchingLength()
    {
        var source = Smb("source-host", "source-share");
        var target = Sftp("sftp-host", "/archives");
        var smb = new MemorySessionFactory();
        var sftp = new MemorySessionFactory { Protocol = FileTransferProtocol.Sftp };
        smb.Put(source, SmbSourcePath, [1, 2, 3, 4], "source-id");
        var adapter = new BackupFileStorageAdapter([smb, sftp]);

        var result = await adapter.TransferAsync(new BackupFileTransferRequest(
            source,
            SmbSourcePath,
            target,
            SftpPartialPath,
            expectedLengthBytes: 4,
            timeoutSeconds: 30));

        Assert.True(result.IsSucceeded);
        Assert.Equal(4, result.Value!.LengthBytes);
        Assert.Equal([1, 2, 3, 4], sftp.Read(target, SftpPartialPath));
        Assert.Equal(1, sftp.CreateNewCount);
        Assert.Equal(0, smb.CreateNewCount);
    }

    [Fact]
    public async Task SmbToSftpWriteFailureIsIndeterminateAndDoesNotOverwriteExistingPartial()
    {
        var source = Smb("source-host", "source-share");
        var target = Sftp("sftp-host", "/archives");
        var smb = new MemorySessionFactory();
        var sftp = new MemorySessionFactory { Protocol = FileTransferProtocol.Sftp, FailWrites = true };
        smb.Put(source, SmbSourcePath, [1, 2, 3, 4]);
        sftp.Put(target, SftpPartialPath, [9, 9, 9, 9]);
        var adapter = new BackupFileStorageAdapter([smb, sftp]);

        var result = await adapter.TransferAsync(new BackupFileTransferRequest(
            source,
            SmbSourcePath,
            target,
            SftpPartialPath,
            expectedLengthBytes: 4,
            timeoutSeconds: 30));

        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(BackupFileStorageFailureCode.FileAlreadyExists, result.Failure!.Code);
        Assert.Equal([9, 9, 9, 9], sftp.Read(target, SftpPartialPath));
        Assert.Equal(0, sftp.CreateNewCount);
    }

    [Fact]
    public async Task SmbToSftpExclusiveCreateThenWriteFailureKeepsPartialIndeterminate()
    {
        var source = Smb("source-host", "source-share");
        var target = Sftp("sftp-host", "/archives");
        var smb = new MemorySessionFactory();
        var sftp = new MemorySessionFactory { Protocol = FileTransferProtocol.Sftp, FailWrites = true };
        smb.Put(source, SmbSourcePath, [1, 2, 3, 4]);
        var adapter = new BackupFileStorageAdapter([smb, sftp]);

        var result = await adapter.TransferAsync(new BackupFileTransferRequest(
            source,
            SmbSourcePath,
            target,
            SftpPartialPath,
            expectedLengthBytes: 4,
            timeoutSeconds: 30));

        Assert.Equal(BackupFileStorageOutcome.Indeterminate, result.Outcome);
        Assert.Equal(BackupFileStorageFailureCode.ConnectionInterrupted, result.Failure!.Code);
        Assert.True(sftp.Exists(target, SftpPartialPath));
    }

    private const string SourcePath = @"\\source-host\source-share\task.bak";
    private const string PartialPath = @"\\target-host\target-share\task.bak.part";
    private const string FinalPath = @"\\target-host\target-share\task.bak";
    private const string SmbSourcePath = SourcePath;
    private const string SftpPartialPath = "/archives/task.bak.part";

    private static BackupFileEndpointInput Smb(string host, string root) =>
        new(FileTransferProtocol.Smb, host, null, root, Guid.NewGuid(), null);

    private static BackupFileEndpointInput Sftp(string host, string root) =>
        new(
            FileTransferProtocol.Sftp,
            host,
            22,
            root,
            Guid.NewGuid(),
            "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

    private sealed class MemorySessionFactory : IFileStorageProtocolSessionFactory
    {
        private readonly Dictionary<string, StoredFile> _files = new(StringComparer.Ordinal);

        public FileTransferProtocol Protocol { get; init; } = FileTransferProtocol.Smb;

        public bool FailWrites { get; init; }

        public bool CompleteRenameThenFail { get; init; }

        public bool FailDirectoryPreparation { get; init; }

        public int CreateNewCount { get; private set; }

        public List<string> PreparedPaths { get; } = [];

        public void Put(
            BackupFileEndpointInput endpoint,
            string path,
            byte[] bytes,
            string? identity = null) =>
            _files[Key(endpoint, path)] = new StoredFile([.. bytes], identity ?? Guid.NewGuid().ToString("N"));

        public byte[] Read(BackupFileEndpointInput endpoint, string path) =>
            [.. _files[Key(endpoint, path)].Bytes];

        public bool Exists(BackupFileEndpointInput endpoint, string path) =>
            _files.ContainsKey(Key(endpoint, path));

        public ValueTask<IFileStorageProtocolSession> OpenAsync(
            BackupFileEndpointInput endpoint,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IFileStorageProtocolSession>(new Session(this, endpoint));

        private static string Key(BackupFileEndpointInput endpoint, string path) =>
            $"{endpoint.Host}|{path}";

        private sealed class Session(
            MemorySessionFactory owner,
            BackupFileEndpointInput endpoint) : IFileStorageProtocolSession
        {
            public ValueTask PrepareParentDirectoryAsync(
                string filePath,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.PreparedPaths.Add(filePath);
                return owner.FailDirectoryPreparation
                    ? ValueTask.FromException(new FileStorageAdapterException(
                        BackupFileStorageFailureCode.ConnectionInterrupted,
                        BackupFileStorageFailurePhase.DirectoryPrepare,
                        indeterminate: true))
                    : ValueTask.CompletedTask;
            }

            public ValueTask<BackupFileMetadata> InspectAsync(
                string path,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(owner._files.TryGetValue(Key(endpoint, path), out var file)
                    ? new BackupFileMetadata(true, true, file.Bytes.LongLength, file.Identity)
                    : new BackupFileMetadata(false, false, null));
            }

            public ValueTask<Stream> OpenReadAsync(
                string path,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult<Stream>(new MemoryStream(
                    owner._files[Key(endpoint, path)].Bytes,
                    writable: false));

            public ValueTask<Stream> CreateNewAsync(
                string path,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Key(endpoint, path);
                if (owner._files.ContainsKey(key))
                {
                    throw new IOException("Synthetic exclusive-create conflict.");
                }

                owner.CreateNewCount++;
                owner._files[key] = new StoredFile([], Guid.NewGuid().ToString("N"));
                return ValueTask.FromResult<Stream>(owner.FailWrites
                    ? new FailingWriteStream(owner, key)
                    : new CommitStream(owner, key));
            }

            public ValueTask RenameNoReplaceAsync(
                string sourcePath,
                string destinationPath,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Key(endpoint, sourcePath);
                var destination = Key(endpoint, destinationPath);
                if (owner._files.ContainsKey(destination))
                {
                    throw new IOException("Synthetic no-replace conflict.");
                }

                owner._files[destination] = owner._files[source];
                owner._files.Remove(source);
                if (owner.CompleteRenameThenFail)
                {
                    throw new IOException("Synthetic response loss.");
                }

                return ValueTask.CompletedTask;
            }

            public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner._files.Remove(Key(endpoint, path));
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private class CommitStream(MemorySessionFactory owner, string key) : MemoryStream
        {
            public override ValueTask DisposeAsync()
            {
                Commit();
                return base.DisposeAsync();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Commit();
                }

                base.Dispose(disposing);
            }

            private void Commit()
            {
                if (TryGetBuffer(out var buffer))
                {
                    owner._files[key] = new StoredFile(
                        buffer.AsSpan(0, checked((int)Length)).ToArray(),
                        owner._files[key].Identity);
                }
            }
        }

        private sealed class FailingWriteStream(MemorySessionFactory owner, string key)
            : CommitStream(owner, key)
        {
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new IOException("Synthetic write failure.");

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default) =>
                ValueTask.FromException(new IOException("Synthetic write failure."));
        }

        private sealed record StoredFile(byte[] Bytes, string Identity);
    }
}
