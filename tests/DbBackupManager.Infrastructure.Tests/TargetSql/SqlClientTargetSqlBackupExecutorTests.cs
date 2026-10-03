using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.TargetSql;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class SqlClientTargetSqlBackupExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RtmBackupRequiresSnapshotOptInBeforeInvocation(bool enabled)
    {
        var session = new StubBackupSession(new TargetSqlServerInfo("10.50.1600.1", "RTM", "synthetic", 3, 10, true));
        var executor = new SqlClientTargetSqlBackupExecutor(StubCredentialResolver.Succeeded(), new StubBackupSessionFactory(session));
        var input = new TargetSqlConnectionInput("synthetic", Guid.NewGuid(), true, false, null, 15,
            enabled, enabled ? "legacy fixture" : null);
        var result = await executor.ExecuteFullBackupAsync(input, CreateRequest(useCompression: true));
        Assert.Equal(enabled, result.IsSucceeded);
        Assert.Equal(enabled ? 1 : 0, session.BackupInvocationCount);
    }

    [Fact]
    public async Task MissingCredentialIsConfirmedBeforeConnectionOpen()
    {
        var resolver = StubCredentialResolver.Failed(TargetSqlFailureCode.CredentialUnavailable);
        var factory = new StubBackupSessionFactory(new StubBackupSession(SupportedServer()));
        var executor = new SqlClientTargetSqlBackupExecutor(resolver, factory);

        var result = await executor.ExecuteFullBackupAsync(CreateConnection(), CreateRequest());

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.CredentialUnavailable, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.CredentialResolution, result.Failure.Phase);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task ConnectionFailureIsConfirmedAndCredentialIsDisposed()
    {
        var resolver = StubCredentialResolver.Succeeded();
        var factory = StubBackupSessionFactory.Failed(TargetSqlFailureCode.AuthenticationFailed);
        var executor = new SqlClientTargetSqlBackupExecutor(resolver, factory);

        var result = await executor.ExecuteFullBackupAsync(CreateConnection(), CreateRequest());

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.AuthenticationFailed, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.ConnectionOpen, result.Failure.Phase);
        Assert.Throws<ObjectDisposedException>(resolver.LastCredential!.CreateSqlCredential);
    }

    [Fact]
    public async Task UnsupportedCompressionStopsBeforeBackupInvocation()
    {
        var session = new StubBackupSession(SupportedServer(supportsCompression: false));
        var executor = new SqlClientTargetSqlBackupExecutor(
            StubCredentialResolver.Succeeded(),
            new StubBackupSessionFactory(session));

        var result = await executor.ExecuteFullBackupAsync(
            CreateConnection(),
            CreateRequest(useCompression: true));

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.CompressionUnsupported, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.ServerProbe, result.Failure.Phase);
        Assert.Equal(0, session.BackupInvocationCount);
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task CancellationObservedBeforeInvocationIsConfirmed()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new StubBackupSession(SupportedServer())
        {
            AfterServerRead = cancellation.Cancel,
        };
        var executor = new SqlClientTargetSqlBackupExecutor(
            StubCredentialResolver.Succeeded(),
            new StubBackupSessionFactory(session));

        var result = await executor.ExecuteFullBackupAsync(
            CreateConnection(),
            CreateRequest(),
            cancellation.Token);

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.Cancelled, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.BackupExecution, result.Failure.Phase);
        Assert.Equal(0, session.BackupInvocationCount);
    }

    [Fact]
    public async Task SuccessfulBackupReturnsExecutedOptionEvidenceAndDisposesSession()
    {
        var session = new StubBackupSession(SupportedServer());
        var executor = new SqlClientTargetSqlBackupExecutor(
            StubCredentialResolver.Succeeded(),
            new StubBackupSessionFactory(session));

        var result = await executor.ExecuteFullBackupAsync(
            CreateConnection(),
            CreateRequest(useCompression: true));

        Assert.True(result.IsSucceeded);
        Assert.True(result.Value!.UsedCopyOnly);
        Assert.True(result.Value.UsedChecksum);
        Assert.True(result.Value.UsedCompression);
        Assert.Equal(1, session.BackupInvocationCount);
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task CommandObserverRunsOnlyAfterBackupInvocationStarts()
    {
        var session = new StubBackupSession(SupportedServer());
        var observer = new RecordingCommandObserver(
            () => Assert.Equal(1, session.BackupInvocationCount));
        var executor = new SqlClientTargetSqlBackupExecutor(
            StubCredentialResolver.Succeeded(),
            new StubBackupSessionFactory(session),
            observer);

        var result = await executor.ExecuteFullBackupAsync(
            CreateConnection(),
            CreateRequest());

        Assert.True(result.IsSucceeded);
        Assert.Equal(1, observer.Invocations);
    }

    [Fact]
    public async Task ExplicitServerRejectionAfterInvocationIsConfirmedWithoutRetry()
    {
        var session = new StubBackupSession(SupportedServer())
        {
            BackupException = TargetSqlClientException.Classified(
                TargetSqlFailureCode.BackupDestinationUnavailable),
        };
        var executor = new SqlClientTargetSqlBackupExecutor(
            StubCredentialResolver.Succeeded(),
            new StubBackupSessionFactory(session));

        var result = await executor.ExecuteFullBackupAsync(CreateConnection(), CreateRequest());

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.BackupDestinationUnavailable, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.BackupExecution, result.Failure.Phase);
        Assert.Equal(1, session.BackupInvocationCount);
    }

    [Theory]
    [InlineData(TargetSqlFailureCode.TimedOut)]
    [InlineData(TargetSqlFailureCode.Cancelled)]
    [InlineData(TargetSqlFailureCode.ConnectionInterrupted)]
    [InlineData(TargetSqlFailureCode.TransportSecurityFailed)]
    public async Task AmbiguousFailureAfterInvocationIsIndeterminateWithoutRetry(
        TargetSqlFailureCode failureCode)
    {
        var session = new StubBackupSession(SupportedServer())
        {
            BackupException = TargetSqlClientException.Classified(
                failureCode,
                TargetSqlClientFailureCertainty.Indeterminate),
        };
        var executor = new SqlClientTargetSqlBackupExecutor(
            StubCredentialResolver.Succeeded(),
            new StubBackupSessionFactory(session));

        var result = await executor.ExecuteFullBackupAsync(CreateConnection(), CreateRequest());

        Assert.Equal(TargetSqlOutcome.Indeterminate, result.Outcome);
        Assert.Equal(failureCode, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.BackupExecution, result.Failure.Phase);
        Assert.Equal(1, session.BackupInvocationCount);
    }

    private static TargetSqlConnectionInput CreateConnection()
    {
        return new TargetSqlConnectionInput(
            "synthetic-target",
            Guid.NewGuid(),
            encryptConnection: true,
            trustServerCertificate: false,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 15);
    }

    private static TargetSqlFullBackupRequest CreateRequest(bool useCompression = false)
    {
        return new TargetSqlFullBackupRequest(
            "SyntheticDatabase",
            "D:\\Synthetic\\backup.bak",
            useCopyOnly: true,
            useChecksum: true,
            useCompression,
            commandTimeoutSeconds: 600);
    }

    private static TargetSqlServerInfo SupportedServer(bool supportsCompression = true)
    {
        return new TargetSqlServerInfo(
            "10.50.6542.0",
            "SP3",
            "Synthetic Edition",
            EngineEdition: 3,
            MajorVersion: 10,
            supportsCompression);
    }

    private sealed class StubCredentialResolver : ITargetSqlCredentialResolver
    {
        private readonly TargetSqlFailureCode? _failureCode;

        private StubCredentialResolver(TargetSqlFailureCode? failureCode)
        {
            _failureCode = failureCode;
        }

        public TargetSqlCredentialLease? LastCredential { get; private set; }

        public static StubCredentialResolver Succeeded()
        {
            return new StubCredentialResolver(null);
        }

        public static StubCredentialResolver Failed(TargetSqlFailureCode failureCode)
        {
            return new StubCredentialResolver(failureCode);
        }

        public ValueTask<TargetSqlCredentialResolution> ResolveSqlPasswordAsync(
            Guid credentialReferenceId,
            CancellationToken cancellationToken)
        {
            if (_failureCode is { } failureCode)
            {
                return ValueTask.FromResult(TargetSqlCredentialResolution.Failed(failureCode));
            }

            LastCredential = TargetSqlCredentialLease.CreateAndClear(
                "synthetic-login",
                "synthetic-password".ToCharArray());
            return ValueTask.FromResult(TargetSqlCredentialResolution.Succeeded(LastCredential));
        }
    }

    private sealed class StubBackupSessionFactory : ITargetSqlBackupClientSessionFactory
    {
        private readonly ITargetSqlBackupClientSession? _session;
        private readonly TargetSqlFailureCode? _failureCode;

        public StubBackupSessionFactory(ITargetSqlBackupClientSession session)
        {
            _session = session;
        }

        private StubBackupSessionFactory(TargetSqlFailureCode failureCode)
        {
            _failureCode = failureCode;
        }

        public int OpenCount { get; private set; }

        public static StubBackupSessionFactory Failed(TargetSqlFailureCode failureCode)
        {
            return new StubBackupSessionFactory(failureCode);
        }

        public Task<ITargetSqlBackupClientSession> OpenAsync(
            TargetSqlConnectionInput input,
            TargetSqlCredentialLease credential,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            return _failureCode is { } failureCode
                ? Task.FromException<ITargetSqlBackupClientSession>(
                    TargetSqlClientException.Classified(failureCode))
                : Task.FromResult(_session!);
        }
    }

    private sealed class StubBackupSession(TargetSqlServerInfo serverInfo)
        : ITargetSqlBackupClientSession
    {
        public Action? AfterServerRead { get; init; }

        public Exception? BackupException { get; init; }

        public int BackupInvocationCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public Task<TargetSqlServerInfo> ReadServerInfoAsync(CancellationToken cancellationToken)
        {
            AfterServerRead?.Invoke();
            return Task.FromResult(serverInfo);
        }

        public Task<TargetSqlFullBackupCompletion> ExecuteFullBackupAsync(
            TargetSqlFullBackupRequest request,
            CancellationToken cancellationToken)
        {
            BackupInvocationCount++;
            return BackupException is null
                ? Task.FromResult(new TargetSqlFullBackupCompletion(
                    request.UseCopyOnly,
                    request.UseChecksum,
                    request.UseCompression))
                : Task.FromException<TargetSqlFullBackupCompletion>(BackupException);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCommandObserver(Action onStarted) : ITargetSqlBackupCommandObserver
    {
        public int Invocations { get; private set; }

        public ValueTask OnCommandStartedAsync(CancellationToken cancellationToken)
        {
            onStarted();
            Invocations++;
            return ValueTask.CompletedTask;
        }
    }
}
