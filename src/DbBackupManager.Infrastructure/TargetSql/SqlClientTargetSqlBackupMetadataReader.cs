using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Infrastructure.TargetSql;

internal interface ITargetSqlMetadataSession
{
    Task<TargetSqlBackupMetadataEvidence> ReadMetadataAsync(TargetSqlBackupMetadataRequest request,
        TargetSqlServerInfo server, CancellationToken cancellationToken);
}

internal sealed class SqlClientTargetSqlBackupMetadataReader(
    ITargetSqlCredentialResolver resolver, ITargetSqlClientSessionFactory factory) : ITargetSqlBackupMetadataReader
{
    public async Task<TargetSqlBackupMetadataEvidence> ReadAsync(TargetSqlConnectionInput connection,
        TargetSqlBackupMetadataRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            using var resolution = await resolver.ResolveSqlPasswordAsync(connection.CredentialReferenceId, deadline.Token);
            if (resolution.FailureCode is { } failure) return Failed(failure);
            await using var session = await factory.OpenAsync(connection, resolution.Credential!, deadline.Token);
            var server = await session.ReadServerInfoAsync(deadline.Token);
            if (!TargetSqlServerInfoMapper.IsSupported(server, connection.AllowLegacyTls))
                return Failed(TargetSqlFailureCode.UnsupportedServerVersion);
            if (session is not ITargetSqlMetadataSession metadata) return Failed(TargetSqlFailureCode.InvalidResponse);
            var evidence = await metadata.ReadMetadataAsync(request, server, deadline.Token);
            if (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                evidence = evidence with
                {
                    Header = TimedOut(evidence.Header),
                    History = TimedOut(evidence.History),
                    Active = evidence.Active.FailureCode == TargetSqlFailureCode.Cancelled
                        ? evidence.Active with { FailureCode = TargetSqlFailureCode.TimedOut } : evidence.Active,
                    ReferencedFulls = evidence.ReferencedFulls.Select(TimedOut).ToArray()
                };
            return evidence;
        }
        catch (OperationCanceledException)
        { return Failed(cancellationToken.IsCancellationRequested ? TargetSqlFailureCode.Cancelled : TargetSqlFailureCode.TimedOut); }
        catch (TargetSqlClientException exception) { return Failed(exception.FailureCode); }
        static BackupMetadataSourceEvidence TimedOut(BackupMetadataSourceEvidence source) =>
            source.FailureCode == TargetSqlFailureCode.Cancelled ? source with { FailureCode = TargetSqlFailureCode.TimedOut } : source;
    }

    internal static BaselineEvidenceStatus Status(TargetSqlFailureCode code) =>
        code == TargetSqlFailureCode.AuthorizationDenied ? BaselineEvidenceStatus.PermissionDenied : BaselineEvidenceStatus.MissingFields;
    internal static BackupMetadataSourceEvidence FailedSource(BackupSetEvidenceSource source, TargetSqlFailureCode code) =>
        new(source, new(), Status(code), [new("source", BackupMetadataReadProblem.ReadFailed)], code);
    internal static TargetSqlBackupMetadataEvidence Failed(TargetSqlFailureCode code) => new(
        FailedSource(BackupSetEvidenceSource.BackupHeader, code), FailedSource(BackupSetEvidenceSource.Msdb, code),
        new(new(new(null, null), null, [], Status(code)), null, [new("source", BackupMetadataReadProblem.ReadFailed)], code),
        [], null, null);
}
