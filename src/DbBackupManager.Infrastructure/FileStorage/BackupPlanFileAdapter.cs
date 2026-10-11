using System.Security.Cryptography;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Infrastructure.FileStorage;

internal sealed class BackupPlanFileAdapter(IEnumerable<IFileStorageProtocolSessionFactory> factories)
    : IBackupFileContentVerifier, IBackupPlanFileTransferExecutor
{
    private readonly IFileStorageProtocolSessionFactory[] _factories = factories.ToArray();
    public Task<BackupFileStorageResult<IVerifiedBackupFileHandle>> VerifyExistingAsync(BackupContentVerificationRequest request,
        CancellationToken cancellationToken = default) => OpenVerifiedAsync(request, cancellationToken);

    public async Task<BackupFileStorageResult<IVerifiedBackupFileHandle>> OpenVerifiedAsync(BackupContentVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || request.Endpoint.Protocol != FileTransferProtocol.Smb) return Unsupported<IVerifiedBackupFileHandle>();
        using var deadline = Deadline(request, cancellationToken);
        IFileStorageProtocolSession? session = null;
        NativePlanFileGuard? file = null;
        try
        {
            session = await Factory(request.Endpoint).OpenAsync(request.Endpoint, deadline.Token);
            if (session is not IProtectedPlanFileSession guarded) return Unsupported<IVerifiedBackupFileHandle>();
            file = guarded.OpenGuarded(request.Path, false, false);
            var content = await file.HashAsync(deadline.Token);
            if (request.ExpectedContent is { } expected && (!expected.MatchesContent(content) || !expected.PreservesKnownObjectId(content)))
                return BackupFileStorageResult.ConfirmedFailure<IVerifiedBackupFileHandle>(BackupFileStorageFailureCode.SourceChanged, BackupFileStorageFailurePhase.SourceInspection);
            var handle = new Handle(request.Endpoint, request.Path, file, content, session);
            file = null; session = null;
            return BackupFileStorageResult.Succeeded<IVerifiedBackupFileHandle>(handle);
        }
        catch (Exception exception) when (CanClassify(exception)) { return Failure<IVerifiedBackupFileHandle>(exception, cancellationToken); }
        finally
        {
            if (file is not null) await file.DisposeAsync();
            if (session is not null) await session.DisposeAsync();
        }
    }

    public async Task<BackupFileStorageResult<BackupFileMutationReceipt>> CheckCapabilitiesAsync(BackupContentVerificationRequest target,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || target.Endpoint.Protocol != FileTransferProtocol.Smb) return Unsupported<BackupFileMutationReceipt>();
        // 能力检查不把配置的协议名称当成已满足契约；只操作本次随机探测对象。
        using var deadline = Deadline(target, cancellationToken);
        var parent = target.Path[..target.Path.LastIndexOf('\\')];
        var probePath = $"{parent}\\.dbbackup-capability-{Guid.NewGuid():N}";
        var renamedPath = probePath + ".renamed";
        try
        {
            await using var session = await Factory(target.Endpoint).OpenAsync(target.Endpoint, deadline.Token);
            if (session is not IProtectedPlanFileSession guarded) return Unsupported<BackupFileMutationReceipt>();
            await session.PrepareParentDirectoryAsync(probePath, deadline.Token);
            string? probeId = null;
            try
            {
                await using (var created = guarded.OpenGuarded(probePath, true, false))
                {
                    probeId = SmbFileOperations.TryGetIdentity(created.Stream.SafeFileHandle);
                    await created.Stream.WriteAsync(new byte[] { 1 }, deadline.Token);
                    await created.Stream.FlushAsync(deadline.Token);
                    _ = await created.HashAsync(deadline.Token);
                    guarded.VerifyGuardedCapabilities(created);
                }
                await using (var file = guarded.OpenGuarded(probePath, false, true))
                {
                    var before = await file.HashAsync(deadline.Token);
                    guarded.RenameGuarded(file, renamedPath);
                    var after = await file.HashAsync(deadline.Token);
                    guarded.VerifyGuardedCapabilities(file);
                    if (!before.MatchesContent(after) || !before.PreservesKnownObjectId(after) || !file.IsProtected)
                        return Unsupported<BackupFileMutationReceipt>();
                }
                return BackupFileStorageResult.Succeeded(new BackupFileMutationReceipt());
            }
            finally
            {
                // 仅清理本次能力探测的随机对象，绝不触碰 Attempt 的源、.part 或正式文件。
                foreach (var path in new[] { probePath, renamedPath })
                    if (probeId is not null && (await session.InspectAsync(path, CancellationToken.None)) is { Exists: true } found && found.Identity == probeId)
                    {
                        await using var ownedProbe = guarded.OpenGuarded(path, false, true);
                        ownedProbe.DeleteOwnedCapabilityProbe(probeId);
                    }
            }
        }
        catch (Exception exception) when (CanClassify(exception)) { return Failure<BackupFileMutationReceipt>(exception, cancellationToken); }
    }

    public async Task<BackupFileStorageResult<VerifiedBackupTransferReceipt>> TransferVerifiedAsync(Guid mutationId,
        IVerifiedBackupFileHandle source, BackupContentVerificationRequest target, CancellationToken cancellationToken = default)
    {
        if (mutationId == Guid.Empty) throw new ArgumentException("传输操作标识不能为空。");
        if (!OperatingSystem.IsWindows() || source is not Handle handle || !handle.GuardsExactPathUntilCommit
            || target.Endpoint.Protocol != FileTransferProtocol.Smb) return Unsupported<VerifiedBackupTransferReceipt>();
        using var deadline = Deadline(target, cancellationToken);
        try
        {
            await using var session = await Factory(target.Endpoint).OpenAsync(target.Endpoint, deadline.Token);
            if (session is not IProtectedPlanFileSession guarded) return Unsupported<VerifiedBackupTransferReceipt>();
            await session.PrepareParentDirectoryAsync(target.Path, deadline.Token);
            await using var destination = guarded.OpenGuarded(target.Path, true, false);
            using var sentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            handle.File.Stream.Position = 0;
            long length = 0;
            int read;
            while ((read = await handle.File.Stream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                await destination.Stream.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                sentHash.AppendData(buffer, 0, read);
                length += read;
            }
            await destination.Stream.FlushAsync(deadline.Token);
            destination.Stream.Flush(flushToDisk: true);
            var sent = new BackupContentEvidence
            {
                State = BackupContentState.Verified,
                DigestAlgorithm = "SHA256",
                DigestHex = Convert.ToHexStringLower(sentHash.GetHashAndReset()),
                LengthBytes = length,
                Protection = BackupObjectProtection.Unknown
            };
            var sourceContent = await handle.File.HashAsync(deadline.Token);
            var destinationContent = await destination.HashAsync(deadline.Token);
            var receipt = new VerifiedBackupTransferReceipt(sourceContent, sent, destinationContent);
            return receipt.MatchesVerifiedSource(source.Content) ? BackupFileStorageResult.Succeeded(receipt)
                : BackupFileStorageResult.Indeterminate<VerifiedBackupTransferReceipt>(BackupFileStorageFailureCode.SourceChanged, BackupFileStorageFailurePhase.DataTransfer);
        }
        catch (Exception exception) when (CanClassify(exception))
        { return Failure<VerifiedBackupTransferReceipt>(exception, cancellationToken, written: true); }
    }

    public async Task<BackupFileStorageResult<VerifiedBackupRenameReceipt>> RenameAndVerifyAsync(Guid mutationId,
        BackupContentVerificationRequest partialFile, string finalPath, CancellationToken cancellationToken = default)
    {
        if (mutationId == Guid.Empty) throw new ArgumentException("确认操作标识不能为空。");
        if (!OperatingSystem.IsWindows() || partialFile.Endpoint.Protocol != FileTransferProtocol.Smb) return Unsupported<VerifiedBackupRenameReceipt>();
        using var deadline = Deadline(partialFile, cancellationToken);
        IFileStorageProtocolSession? session = null;
        NativePlanFileGuard? file = null;
        try
        {
            session = await Factory(partialFile.Endpoint).OpenAsync(partialFile.Endpoint, deadline.Token);
            if (session is not IProtectedPlanFileSession guarded) return Unsupported<VerifiedBackupRenameReceipt>();
            file = guarded.OpenGuarded(partialFile.Path, false, true);
            var before = await file.HashAsync(deadline.Token);
            if (partialFile.ExpectedContent is not { } expected || !expected.MatchesContent(before) || !expected.PreservesKnownObjectId(before))
                return BackupFileStorageResult.ConfirmedFailure<VerifiedBackupRenameReceipt>(BackupFileStorageFailureCode.SourceChanged, BackupFileStorageFailurePhase.Rename);
            guarded.RenameGuarded(file, finalPath);
            var final = await file.HashAsync(deadline.Token);
            var partExists = (await session.InspectAsync(partialFile.Path, deadline.Token)).Exists;
            var handle = new Handle(partialFile.Endpoint, finalPath, file, final, session);
            file = null; session = null;
            return BackupFileStorageResult.Succeeded(new VerifiedBackupRenameReceipt(before, final, partExists, true, handle));
        }
        catch (Exception exception) when (CanClassify(exception)) { return Failure<VerifiedBackupRenameReceipt>(exception, cancellationToken, written: true); }
        finally
        {
            if (file is not null) await file.DisposeAsync();
            if (session is not null) await session.DisposeAsync();
        }
    }
    private IFileStorageProtocolSessionFactory Factory(BackupFileEndpointInput endpoint) => _factories.Single(x => x.Protocol == endpoint.Protocol);
    private static CancellationTokenSource Deadline(BackupContentVerificationRequest request, CancellationToken token)
    {
        if (request.TaskId == Guid.Empty || request.AttemptId == Guid.Empty || request.TimeoutSeconds is < 1 or > 86400)
            throw new ArgumentException("文件证据请求无效。");
        var result = CancellationTokenSource.CreateLinkedTokenSource(token);
        result.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        return result;
    }
    private static bool CanClassify(Exception exception) => exception is IOException or UnauthorizedAccessException or OperationCanceledException or FileStorageAdapterException;
    private static BackupFileStorageResult<T> Unsupported<T>() where T : class =>
        BackupFileStorageResult.ConfirmedFailure<T>(BackupFileStorageFailureCode.PlatformUnsupported, BackupFileStorageFailurePhase.ConnectionOpen);
    private static BackupFileStorageResult<T> Failure<T>(Exception exception, CancellationToken caller, bool written = false) where T : class
    {
        var code = exception switch
        {
            FileStorageAdapterException failure => failure.Code,
            OperationCanceledException => caller.IsCancellationRequested ? BackupFileStorageFailureCode.Cancelled : BackupFileStorageFailureCode.TimedOut,
            FileNotFoundException => BackupFileStorageFailureCode.FileNotFound,
            UnauthorizedAccessException => BackupFileStorageFailureCode.AuthorizationDenied,
            _ => BackupFileStorageFailureCode.OperationRejected
        };
        return written ? BackupFileStorageResult.Indeterminate<T>(code, BackupFileStorageFailurePhase.DataTransfer)
            : BackupFileStorageResult.ConfirmedFailure<T>(code, BackupFileStorageFailurePhase.SourceInspection);
    }

    private sealed class Handle(BackupFileEndpointInput endpoint, string logicalPath, NativePlanFileGuard file,
        BackupContentEvidence content, IFileStorageProtocolSession session) : IVerifiedBackupFileHandle
    {
        public NativePlanFileGuard File => file;
        public BackupFileEndpointInput Endpoint => endpoint;
        public string Path => logicalPath;
        public BackupContentEvidence Content => content;
        public bool GuardsExactPathUntilCommit => OperatingSystem.IsWindows() && file.IsProtected;
        public async ValueTask DisposeAsync()
        {
            try { if (OperatingSystem.IsWindows()) await file.DisposeAsync(); }
            finally { await session.DisposeAsync(); }
        }
    }
}
