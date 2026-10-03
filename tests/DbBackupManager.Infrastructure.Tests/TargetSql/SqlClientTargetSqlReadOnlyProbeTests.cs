using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.TargetSql;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class SqlClientTargetSqlReadOnlyProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RtmProbeRequiresExplicitOptIn(bool enabled)
    {
        var server = new TargetSqlServerInfo("10.50.1600.1", "RTM", "synthetic", 3, 10, true);
        var probe = new SqlClientTargetSqlReadOnlyProbe(StubCredentialResolver.Succeeded(), new StubSessionFactory(new StubSession(server)));
        var input = new TargetSqlConnectionInput("synthetic", Guid.NewGuid(), true, false, null, 15,
            enabled, enabled ? "legacy fixture" : null);
        var result = await probe.ProbeServerAsync(input);
        Assert.Equal(enabled, result.IsSucceeded);
        var catalog = await probe.DiscoverDatabasesAsync(input);
        Assert.Equal(enabled, catalog.IsSucceeded);
    }

    [Fact]
    public void ProbeAdapterCannotBeUsedAsBackupExecutor()
    {
        var adapterType = typeof(SqlClientTargetSqlReadOnlyProbe);

        Assert.True(typeof(ITargetSqlReadOnlyProbe).IsAssignableFrom(adapterType));
        Assert.False(typeof(ITargetSqlBackupExecutor).IsAssignableFrom(adapterType));
        Assert.DoesNotContain(
            typeof(ITargetSqlClientSession).GetMethods(),
            method => string.Equals(
                method.Name,
                nameof(ITargetSqlBackupExecutor.ExecuteFullBackupAsync),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingCredentialFailsBeforeOpeningTargetConnection()
    {
        var resolver = StubCredentialResolver.Failed(TargetSqlFailureCode.CredentialUnavailable);
        var sessionFactory = new StubSessionFactory(new StubSession(SupportedServer()));
        var probe = new SqlClientTargetSqlReadOnlyProbe(resolver, sessionFactory);

        var result = await probe.ProbeServerAsync(CreateConnectionInput());

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.CredentialUnavailable, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.CredentialResolution, result.Failure.Phase);
        Assert.Equal(0, sessionFactory.OpenCount);
    }

    [Fact]
    public async Task ConnectionFailureUsesStableCodeAndDisposesCredential()
    {
        var resolver = StubCredentialResolver.Succeeded();
        var sessionFactory = StubSessionFactory.Failed(TargetSqlFailureCode.TransportSecurityFailed);
        var probe = new SqlClientTargetSqlReadOnlyProbe(resolver, sessionFactory);

        var result = await probe.ProbeServerAsync(CreateConnectionInput());

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.TransportSecurityFailed, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.ConnectionOpen, result.Failure.Phase);
        Assert.Throws<ObjectDisposedException>(resolver.LastCredential!.CreateSqlCredential);
    }

    [Fact]
    public async Task UnsupportedServerStopsBeforeDatabaseDiscovery()
    {
        var session = new StubSession(new TargetSqlServerInfo(
            "10.0.6000.29",
            "SP4",
            "Synthetic Edition",
            EngineEdition: 3,
            MajorVersion: 10,
            SupportsBackupCompression: false));
        var probe = new SqlClientTargetSqlReadOnlyProbe(
            StubCredentialResolver.Succeeded(),
            new StubSessionFactory(session));

        var result = await probe.DiscoverDatabasesAsync(CreateConnectionInput());

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.UnsupportedServerVersion, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.ServerProbe, result.Failure.Phase);
        Assert.Equal(0, session.DatabaseReadCount);
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task ServerProbeFailureKeepsItsProbePhase()
    {
        var session = new StubSession(SupportedServer())
        {
            ServerException = TargetSqlClientException.Classified(
                TargetSqlFailureCode.ConnectionInterrupted),
        };
        var probe = new SqlClientTargetSqlReadOnlyProbe(
            StubCredentialResolver.Succeeded(),
            new StubSessionFactory(session));

        var result = await probe.ProbeServerAsync(CreateConnectionInput());

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.ConnectionInterrupted, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.ServerProbe, result.Failure.Phase);
    }

    [Fact]
    public async Task DiscoveryReturnsDefensiveCatalogForSupportedServer()
    {
        var databases = new TargetSqlDatabaseCatalog(
        [
            new TargetSqlDatabaseInfo(
                "SyntheticDatabase",
                TargetSqlDatabaseState.Online,
                TargetSqlRecoveryModel.Full,
                IsSystemDatabase: false,
                IsSnapshot: false,
                CanBeManaged: true),
        ]);
        var session = new StubSession(SupportedServer(), databases);
        var probe = new SqlClientTargetSqlReadOnlyProbe(
            StubCredentialResolver.Succeeded(),
            new StubSessionFactory(session));

        var result = await probe.DiscoverDatabasesAsync(CreateConnectionInput());

        Assert.True(result.IsSucceeded);
        Assert.Equal("SyntheticDatabase", Assert.Single(result.Value!.Databases).Name);
        Assert.Equal(1, session.ServerReadCount);
        Assert.Equal(1, session.DatabaseReadCount);
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task DiscoveryCancellationUsesDiscoveryPhaseAndNeverBecomesIndeterminate()
    {
        var session = new StubSession(SupportedServer())
        {
            DatabaseException = new OperationCanceledException(),
        };
        var probe = new SqlClientTargetSqlReadOnlyProbe(
            StubCredentialResolver.Succeeded(),
            new StubSessionFactory(session));

        var result = await probe.DiscoverDatabasesAsync(CreateConnectionInput());

        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, result.Outcome);
        Assert.Equal(TargetSqlFailureCode.Cancelled, result.Failure!.Code);
        Assert.Equal(TargetSqlFailurePhase.DatabaseDiscovery, result.Failure.Phase);
    }

    [Fact]
    public async Task VerificationUsesReadOnlyPortAndReturnsChecksumEvidence()
    {
        var session = new StubSession(SupportedServer());
        var probe = new SqlClientTargetSqlReadOnlyProbe(
            StubCredentialResolver.Succeeded(),
            new StubSessionFactory(session));
        var request = new TargetSqlBackupVerificationRequest(
            "synthetic.bak",
            useChecksum: true,
            commandTimeoutSeconds: 600);

        var result = await probe.VerifyBackupAsync(CreateConnectionInput(), request);

        Assert.True(result.IsSucceeded);
        Assert.True(result.Value!.UsedChecksum);
        Assert.Equal(1, session.VerifyCount);
    }

    [Fact]
    public async Task IdentityInspectionUsesDedicatedReadOnlyPort()
    {
        var session = new StubSession(SupportedServer());
        var adapter = new SqlClientTargetSqlReadOnlyProbe(
            StubCredentialResolver.Succeeded(),
            new StubSessionFactory(session));

        var result = await adapter.InspectBackupIdentityAsync(
            CreateConnectionInput(),
            new TargetSqlBackupIdentityRequest("SyntheticDatabase", "synthetic.bak", 600));

        Assert.True(result.IsSucceeded);
        Assert.Equal(TargetSqlBackupIdentityStatus.CompletedMatching, result.Value!.Status);
        Assert.Equal(1, session.IdentityInspectionCount);
        Assert.Equal(0, session.VerifyCount);
        Assert.True(session.IsDisposed);
    }

    private static TargetSqlConnectionInput CreateConnectionInput()
    {
        return new TargetSqlConnectionInput(
            "synthetic-target",
            Guid.NewGuid(),
            encryptConnection: true,
            trustServerCertificate: false,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 15);
    }

    private static TargetSqlServerInfo SupportedServer()
    {
        return new TargetSqlServerInfo(
            "10.50.6542.0",
            "SP3",
            "Synthetic Edition",
            EngineEdition: 3,
            MajorVersion: 10,
            SupportsBackupCompression: true);
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

    private sealed class StubSessionFactory : ITargetSqlClientSessionFactory
    {
        private readonly ITargetSqlClientSession? _session;
        private readonly TargetSqlFailureCode? _failureCode;

        public StubSessionFactory(ITargetSqlClientSession session)
        {
            _session = session;
        }

        private StubSessionFactory(TargetSqlFailureCode failureCode)
        {
            _failureCode = failureCode;
        }

        public int OpenCount { get; private set; }

        public static StubSessionFactory Failed(TargetSqlFailureCode failureCode)
        {
            return new StubSessionFactory(failureCode);
        }

        public Task<ITargetSqlClientSession> OpenAsync(
            TargetSqlConnectionInput input,
            TargetSqlCredentialLease credential,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            if (_failureCode is { } failureCode)
            {
                throw TargetSqlClientException.Classified(failureCode);
            }

            return Task.FromResult(_session!);
        }
    }

    private sealed class StubSession : ITargetSqlClientSession
    {
        private readonly TargetSqlServerInfo _serverInfo;
        private readonly TargetSqlDatabaseCatalog _catalog;

        public StubSession(
            TargetSqlServerInfo serverInfo,
            TargetSqlDatabaseCatalog? catalog = null)
        {
            _serverInfo = serverInfo;
            _catalog = catalog ?? new TargetSqlDatabaseCatalog([]);
        }

        public Exception? DatabaseException { get; init; }

        public Exception? ServerException { get; init; }

        public int ServerReadCount { get; private set; }

        public int DatabaseReadCount { get; private set; }

        public int VerifyCount { get; private set; }

        public int IdentityInspectionCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public Task<TargetSqlServerInfo> ReadServerInfoAsync(CancellationToken cancellationToken)
        {
            ServerReadCount++;
            return ServerException is null
                ? Task.FromResult(_serverInfo)
                : Task.FromException<TargetSqlServerInfo>(ServerException);
        }

        public Task<TargetSqlDatabaseCatalog> ReadDatabasesAsync(
            CancellationToken cancellationToken)
        {
            DatabaseReadCount++;
            return DatabaseException is null
                ? Task.FromResult(_catalog)
                : Task.FromException<TargetSqlDatabaseCatalog>(DatabaseException);
        }

        public Task<TargetSqlBackupVerification> VerifyBackupAsync(
            TargetSqlBackupVerificationRequest request,
            CancellationToken cancellationToken)
        {
            VerifyCount++;
            return Task.FromResult(new TargetSqlBackupVerification(request.UseChecksum));
        }

        public Task<TargetSqlBackupIdentity> InspectBackupIdentityAsync(
            TargetSqlBackupIdentityRequest request,
            CancellationToken cancellationToken)
        {
            IdentityInspectionCount++;
            return Task.FromResult(new TargetSqlBackupIdentity(
                TargetSqlBackupIdentityStatus.CompletedMatching));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
