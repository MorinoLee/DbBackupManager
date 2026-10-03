using System.Buffers;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Infrastructure.FileStorage;

internal sealed class BackupFileStorageAdapter(
    IEnumerable<IFileStorageProtocolSessionFactory> sessionFactories) :
    IBackupFileStorageProbe,
    IBackupDirectoryPreparer,
    IBackupFileTransferExecutor,
    IBackupFileDeletionExecutor
{
    private readonly Dictionary<FileTransferProtocol, IFileStorageProtocolSessionFactory>
        _sessionFactories = sessionFactories.ToDictionary(factory => factory.Protocol);

    public async Task<BackupFileStorageResult<BackupFileMutationReceipt>> PrepareParentAsync(
        BackupDirectoryPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            using var timeout = CreateTimeout(request.TimeoutSeconds, cancellationToken);
            await using var session = await OpenAsync(request.Endpoint, timeout.Token);
            await session.PrepareParentDirectoryAsync(request.FilePath, timeout.Token);
            return BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance);
        }
        catch (Exception exception)
        {
            // 目录准备可安全重放；即使远端已创建部分层级，也不能升级为业务结果不确定。
            var failure = Failure<BackupFileMutationReceipt>(
                exception,
                BackupFileStorageFailurePhase.DirectoryPrepare,
                mutationStarted: false,
                cancellationToken);
            return BackupFileStorageResult.ConfirmedFailure<BackupFileMutationReceipt>(
                failure.Failure!.Code,
                BackupFileStorageFailurePhase.DirectoryPrepare);
        }
    }

    public async Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
        BackupFileEndpointInput endpoint,
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            var exactPath = RequirePath(path, nameof(path));
            await using var session = await OpenAsync(endpoint, cancellationToken);
            var metadata = await session.InspectAsync(exactPath, cancellationToken);
            return BackupFileStorageResult.Succeeded(metadata);
        }
        catch (Exception exception)
        {
            return Failure<BackupFileMetadata>(
                exception,
                BackupFileStorageFailurePhase.SourceInspection,
                mutationStarted: false,
                cancellationToken);
        }
    }

    public async Task<BackupFileStorageResult<BackupFileTransferReceipt>> TransferAsync(
        BackupFileTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        var mutationStarted = false;
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            using var timeout = CreateTimeout(request.TimeoutSeconds, cancellationToken);
            var token = timeout.Token;
            await using var sourceSession = await OpenAsync(request.SourceEndpoint, token);
            await using var targetSession = await OpenAsync(request.TargetEndpoint, token);

            var sourceBefore = await sourceSession.InspectAsync(request.SourcePath, token);
            var sourceFailure = ValidateSource(sourceBefore, request.ExpectedLengthBytes);
            if (sourceFailure is not null)
            {
                return sourceFailure;
            }

            var targetBefore = await targetSession.InspectAsync(request.TargetPartialPath, token);
            if (targetBefore.Exists)
            {
                return Confirmed<BackupFileTransferReceipt>(
                    BackupFileStorageFailureCode.FileAlreadyExists,
                    BackupFileStorageFailurePhase.TargetCreate);
            }

            long transferred;
            await using (var source = await sourceSession.OpenReadAsync(request.SourcePath, token))
            {
                mutationStarted = true;
                await using (var target = await targetSession.CreateNewAsync(
                    request.TargetPartialPath,
                    token))
                {
                    transferred = await CopyExactAsync(
                        source,
                        target,
                        request.ExpectedLengthBytes,
                        token);
                    await target.FlushAsync(token);
                }
            }

            var sourceAfter = await sourceSession.InspectAsync(request.SourcePath, token);
            if (!sourceAfter.Exists
                || !sourceAfter.IsRegularFile
                || sourceAfter.LengthBytes != request.ExpectedLengthBytes
                || sourceBefore.Identity is not null
                    && !string.Equals(
                        sourceBefore.Identity,
                        sourceAfter.Identity,
                        StringComparison.Ordinal))
            {
                return Confirmed<BackupFileTransferReceipt>(
                    BackupFileStorageFailureCode.SourceChanged,
                    BackupFileStorageFailurePhase.SourceInspection);
            }

            var targetAfter = await targetSession.InspectAsync(request.TargetPartialPath, token);
            if (!targetAfter.Exists
                || !targetAfter.IsRegularFile
                || targetAfter.LengthBytes != request.ExpectedLengthBytes)
            {
                return Confirmed<BackupFileTransferReceipt>(
                    BackupFileStorageFailureCode.LengthMismatch,
                    BackupFileStorageFailurePhase.TargetFlush);
            }

            return BackupFileStorageResult.Succeeded(
                new BackupFileTransferReceipt(transferred));
        }
        catch (Exception exception)
        {
            return Failure<BackupFileTransferReceipt>(
                exception,
                BackupFileStorageFailurePhase.DataTransfer,
                mutationStarted,
                cancellationToken);
        }
    }

    public async Task<BackupFileStorageResult<BackupFileMutationReceipt>> RenameAsync(
        BackupFileRenameRequest request,
        CancellationToken cancellationToken = default)
    {
        var mutationStarted = false;
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            using var timeout = CreateTimeout(request.TimeoutSeconds, cancellationToken);
            var token = timeout.Token;
            await using var session = await OpenAsync(request.Endpoint, token);
            var source = await session.InspectAsync(request.PartialPath, token);
            var sourceFailure = ValidateSource<BackupFileMutationReceipt>(
                source,
                request.ExpectedLengthBytes);
            if (sourceFailure is not null)
            {
                return sourceFailure;
            }

            if ((await session.InspectAsync(request.FinalPath, token)).Exists)
            {
                return Confirmed<BackupFileMutationReceipt>(
                    BackupFileStorageFailureCode.FileAlreadyExists,
                    BackupFileStorageFailurePhase.Rename);
            }

            mutationStarted = true;
            await session.RenameNoReplaceAsync(request.PartialPath, request.FinalPath, token);
            var partialAfter = await session.InspectAsync(request.PartialPath, token);
            var finalAfter = await session.InspectAsync(request.FinalPath, token);
            if (partialAfter.Exists
                || !finalAfter.Exists
                || !finalAfter.IsRegularFile
                || finalAfter.LengthBytes != request.ExpectedLengthBytes)
            {
                return BackupFileStorageResult.Indeterminate<BackupFileMutationReceipt>(
                    BackupFileStorageFailureCode.InvalidResponse,
                    BackupFileStorageFailurePhase.ResponseProcessing);
            }

            return BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance);
        }
        catch (Exception exception)
        {
            return Failure<BackupFileMutationReceipt>(
                exception,
                BackupFileStorageFailurePhase.Rename,
                mutationStarted,
                cancellationToken);
        }
    }

    public async Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(
        BackupFileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var mutationStarted = false;
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            using var timeout = CreateTimeout(request.TimeoutSeconds, cancellationToken);
            var token = timeout.Token;
            await using var session = await OpenAsync(request.Endpoint, token);
            var before = await session.InspectAsync(request.Path, token);
            var sourceFailure = ValidateSource<BackupFileMutationReceipt>(
                before,
                request.ExpectedLengthBytes);
            if (sourceFailure is not null)
            {
                return sourceFailure;
            }

            if (request.ExpectedIdentity is not null
                && !string.Equals(
                    request.ExpectedIdentity,
                    before.Identity,
                    StringComparison.Ordinal))
            {
                return Confirmed<BackupFileMutationReceipt>(
                    BackupFileStorageFailureCode.SourceChanged,
                    BackupFileStorageFailurePhase.Delete);
            }

            mutationStarted = true;
            await session.DeleteAsync(request.Path, token);
            if ((await session.InspectAsync(request.Path, token)).Exists)
            {
                return Confirmed<BackupFileMutationReceipt>(
                    BackupFileStorageFailureCode.OperationRejected,
                    BackupFileStorageFailurePhase.ResponseProcessing);
            }

            return BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance);
        }
        catch (Exception exception)
        {
            return Failure<BackupFileMutationReceipt>(
                exception,
                BackupFileStorageFailurePhase.Delete,
                mutationStarted,
                cancellationToken);
        }
    }

    private ValueTask<IFileStorageProtocolSession> OpenAsync(
        BackupFileEndpointInput endpoint,
        CancellationToken cancellationToken)
    {
        if (!_sessionFactories.TryGetValue(endpoint.Protocol, out var factory))
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.PlatformUnsupported,
                BackupFileStorageFailurePhase.ConnectionOpen);
        }

        return factory.OpenAsync(endpoint, cancellationToken);
    }

    private static async Task<long> CopyExactAsync(
        Stream source,
        Stream target,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > expectedLength)
                {
                    throw new FileStorageAdapterException(
                        BackupFileStorageFailureCode.SourceChanged,
                        BackupFileStorageFailurePhase.DataTransfer);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total != expectedLength)
            {
                throw new FileStorageAdapterException(
                    BackupFileStorageFailureCode.SourceChanged,
                    BackupFileStorageFailurePhase.DataTransfer);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static BackupFileStorageResult<BackupFileTransferReceipt>? ValidateSource(
        BackupFileMetadata metadata,
        long expectedLength) =>
        ValidateSource<BackupFileTransferReceipt>(metadata, expectedLength);

    private static BackupFileStorageResult<T>? ValidateSource<T>(
        BackupFileMetadata metadata,
        long expectedLength)
        where T : class
    {
        if (!metadata.Exists)
        {
            return Confirmed<T>(
                BackupFileStorageFailureCode.FileNotFound,
                BackupFileStorageFailurePhase.SourceInspection);
        }

        if (!metadata.IsRegularFile)
        {
            return Confirmed<T>(
                BackupFileStorageFailureCode.NotRegularFile,
                BackupFileStorageFailurePhase.SourceInspection);
        }

        return metadata.LengthBytes != expectedLength
            ? Confirmed<T>(
                BackupFileStorageFailureCode.LengthMismatch,
                BackupFileStorageFailurePhase.SourceInspection)
            : null;
    }

    private static BackupFileStorageResult<T> Failure<T>(
        Exception exception,
        BackupFileStorageFailurePhase defaultPhase,
        bool mutationStarted,
        CancellationToken callerToken)
        where T : class
    {
        if (exception is FileStorageAdapterException adapter)
        {
            return adapter.Indeterminate
                ? BackupFileStorageResult.Indeterminate<T>(adapter.Code, adapter.Phase)
                : BackupFileStorageResult.ConfirmedFailure<T>(adapter.Code, adapter.Phase);
        }

        if (exception is OperationCanceledException)
        {
            var code = callerToken.IsCancellationRequested
                ? BackupFileStorageFailureCode.Cancelled
                : BackupFileStorageFailureCode.TimedOut;
            return mutationStarted
                ? BackupFileStorageResult.Indeterminate<T>(code, defaultPhase)
                : BackupFileStorageResult.ConfirmedFailure<T>(code, defaultPhase);
        }

        var failureCode = exception switch
        {
            ArgumentException => BackupFileStorageFailureCode.InvalidRequest,
            UnauthorizedAccessException => BackupFileStorageFailureCode.AuthorizationDenied,
            FileNotFoundException => BackupFileStorageFailureCode.FileNotFound,
            DirectoryNotFoundException => BackupFileStorageFailureCode.PathRejected,
            IOException => BackupFileStorageFailureCode.ConnectionInterrupted,
            _ => BackupFileStorageFailureCode.InvalidResponse,
        };
        return mutationStarted && exception is IOException
            ? BackupFileStorageResult.Indeterminate<T>(failureCode, defaultPhase)
            : BackupFileStorageResult.ConfirmedFailure<T>(failureCode, defaultPhase);
    }

    private static BackupFileStorageResult<T> Confirmed<T>(
        BackupFileStorageFailureCode code,
        BackupFileStorageFailurePhase phase)
        where T : class => BackupFileStorageResult.ConfirmedFailure<T>(code, phase);

    private static CancellationTokenSource CreateTimeout(
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return source;
    }

    private static string RequirePath(string? value, string parameterName)
    {
        var path = value?.Trim();
        if (string.IsNullOrEmpty(path) || path.Length > 2_048 || path.Any(char.IsControl))
        {
            throw new ArgumentException("文件路径无效。", parameterName);
        }

        return path;
    }
}
