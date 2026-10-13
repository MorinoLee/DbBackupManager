using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupFileRetentionSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ClearBackupTaskDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;


    [Fact]
    public async Task ScheduledRetentionDoesNotClaimPlanCopies()
    {
        using var provider = database.CreateServiceProvider();
        var store = new BackupFileRetentionStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());
        var graph = await SeedGraphAsync();
        var now = DateTimeOffset.UtcNow;
        var older = await AddLocalFileAsync(graph, now.AddDays(-20), 7);
        var newest = await AddLocalFileAsync(graph, now.AddDays(-2), 7);
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimNextAsync(Claim(now))).Code);
        Assert.Equal(BackupTaskStoreResultCode.Protected, (await store.ClaimFileAsync(older.Id, Claim(now))).Code);
        var files = new FakeRetentionFiles();
        files.Present.Add(older.Path);
        files.Present.Add(newest.Path);
        var runner = new BackupFileRetentionRunner(store, files, files, TimeProvider.System,
            new(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(60), 30));
        Assert.False(await runner.RunOnceAsync());
        Assert.Equal(0, files.Deletes);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.BackupFiles.CountAsync(file => file.Status == BackupFileStatus.Available));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthCursorVisitsEveryFileInDatabaseOrderAndWraps(bool disabled)
    {
        var expected = await SeedHealthFilesAsync();
        if (disabled)
        {
            await using var db = database.CreateContext();
            (await db.DatabaseServers.SingleAsync(server => server.Id == expected[0].DatabaseServerId))
                .SetEnabled(false);
            await db.SaveChangesAsync();
        }
        var budget = new HealthReadCounter();
        var store = new BackupFileRetentionStore(new InterceptingFactory(database, budget));
        Guid? cursor = null;
        for (var i = 0; i <= expected.Length; i++)
        {
            budget.Reset();
            var found = await store.FindNextHealthInspectionAsync(cursor, DateTimeOffset.UtcNow);
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, found.Code);
            Assert.Equal(expected[i % expected.Length].Id, found.Value!.FileId);
            Assert.Equal(disabled, found.Value.Endpoint is null);
            Assert.Equal(1, budget.MaterializedFiles);
            Assert.InRange(budget.FileReads, 1, 2);
            cursor = found.Value.FileId;
        }
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 0)]
    [InlineData("ffffffff-ffff-ffff-ffff-000000000001", 1)]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", 0)]
    public async Task HealthCursorResumesFromUnregisteredIdWithBoundedReads(string cursor, int expectedIndex)
    {
        var expected = await SeedHealthFilesAsync();
        var budget = new HealthReadCounter();
        var store = new BackupFileRetentionStore(new InterceptingFactory(database, budget));

        var found = await store.FindNextHealthInspectionAsync(Guid.Parse(cursor), DateTimeOffset.UtcNow);

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, found.Code);
        Assert.Equal(expected[expectedIndex].Id, found.Value!.FileId);
        Assert.Equal(1, budget.MaterializedFiles);
        Assert.InRange(budget.FileReads, 1, 2);
    }

    [Fact]
    public async Task HealthCursorContinuesAfterCursorFileBecomesMissing()
    {
        var expected = await SeedHealthFilesAsync();
        using var provider = database.CreateServiceProvider();
        var store = new BackupFileRetentionStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());
        var first = (await store.FindNextHealthInspectionAsync(null, DateTimeOffset.UtcNow)).Value!;
        Assert.Equal(BackupTaskStoreResultCode.Succeeded,
            (await store.CommitExternalMissingAsync(first, Guid.NewGuid(), DateTimeOffset.UtcNow)).Code);
        var cursor = first.FileId;
        foreach (var file in new[] { expected[1], expected[2], expected[1] })
        {
            var found = await store.FindNextHealthInspectionAsync(cursor, DateTimeOffset.UtcNow);
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, found.Code);
            Assert.Equal(file.Id, found.Value!.FileId);
            cursor = found.Value.FileId;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthCursorWithNoAvailableFileReturnsNotFoundWithoutMaterializingFiles(bool withCursor)
    {
        var budget = new HealthReadCounter();
        var store = new BackupFileRetentionStore(new InterceptingFactory(database, budget));

        var found = await store.FindNextHealthInspectionAsync(
            withCursor ? Guid.NewGuid() : null, DateTimeOffset.UtcNow);

        Assert.Equal(BackupTaskStoreResultCode.NotFound, found.Code);
        Assert.Null(found.Value);
        Assert.Equal(0, budget.MaterializedFiles);
        Assert.InRange(budget.FileReads, 1, 2);
    }

    [Fact]
    public async Task HealthRunnerFindsMissingFileThatDotNetCursorOrderWouldSkip()
    {
        var expected = await SeedHealthFilesAsync();
        var files = new FakeRetentionFiles();
        files.Present.Add(expected[0].Path);
        files.Present.Add(expected[1].Path);
        using var provider = database.CreateServiceProvider();
        var store = new BackupFileRetentionStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());
        var runner = new BackupFileRetentionRunner(store, files, files, TimeProvider.System,
            new(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(60), 30));

        for (var i = 0; i < expected.Length + 1; i++)
            Assert.False(await runner.RunOnceAsync());

        Assert.Equal(new[] { expected[0].Path, expected[1].Path, expected[2].Path, expected[0].Path },
            files.InspectedPaths);
        Assert.Equal(0, files.Deletes);
        await using var db = database.CreateContext();
        Assert.Equal(BackupFileStatus.Missing,
            (await db.BackupFiles.AsNoTracking().SingleAsync(file => file.Id == expected[2].Id)).Status);
        Assert.Equal(2, await db.BackupFiles.CountAsync(file => file.Status == BackupFileStatus.Available));
    }

    [Fact]
    public async Task HealthCheckMarksAvailableFileMissingWithoutDeleting()
    {
        var files = new FakeRetentionFiles();
        using var provider = database.CreateServiceProvider();
        var store = new BackupFileRetentionStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());
        var graph = await SeedGraphAsync();
        var kept = await AddLocalFileAsync(graph, DateTimeOffset.UtcNow.AddDays(-1), 30);
        files.Present.Add(kept.Path);
        var runner = new BackupFileRetentionRunner(
            store,
            files,
            files,
            TimeProvider.System,
            new(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(60), 30));

        Assert.False(await runner.RunOnceAsync(CancellationToken.None));
        files.Present.Remove(kept.Path);
        Assert.False(await runner.RunOnceAsync(CancellationToken.None));

        await using var db = database.CreateContext();
        var stored = await db.BackupFiles.SingleAsync(file => file.Id == kept.Id);
        Assert.Equal(BackupFileStatus.Missing, stored.Status);
        Assert.Equal(0, files.Deletes);
        Assert.Contains(
            await db.BackupFileStateChanges.AsNoTracking().Select(change => change.ReasonCode).ToArrayAsync(),
            reason => reason == "file.health_missing");
    }



    private static ClaimBackupFileDeletionCommand Claim(DateTimeOffset now, Guid? leaseToken = null) =>
        new(
            leaseToken ?? Guid.NewGuid(),
            Guid.NewGuid(),
            "retention-test",
            now,
            now.AddMinutes(5));

    private async Task<Graph> SeedGraphAsync(bool withSecondTarget = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var staging = new CredentialReference(
            Guid.NewGuid(),
            $"staging-{suffix}",
            CredentialKind.SmbPassword,
            "synthetic",
            "synthetic-cipher",
            "dp-smb-password-v1");
        var sql = new CredentialReference(
            Guid.NewGuid(),
            $"sql-{suffix}",
            CredentialKind.SqlPassword,
            "synthetic",
            "synthetic-cipher",
            "dp-sql-password-v1");
        var remote = new CredentialReference(
            Guid.NewGuid(),
            $"remote-{suffix}",
            CredentialKind.SmbPassword,
            "synthetic-remote",
            "synthetic-cipher",
            "dp-smb-password-v1");
        var server = new DatabaseServer(
            Guid.NewGuid(),
            $"server-{suffix}",
            @"D:\Synthetic",
            new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", staging.Id, null));
        var instance = new DatabaseInstance(
            Guid.NewGuid(),
            server.Id,
            "instance",
            $"synthetic-{suffix}",
            sql.Id,
            true,
            false,
            null,
            15);
        var managed = new ManagedDatabase(
            Guid.NewGuid(),
            instance.Id,
            $"db-{suffix}",
            false,
            true,
            DateTimeOffset.UtcNow);
        managed.SetManaged(true);
        var target = new StorageTarget(
            Guid.NewGuid(),
            $"target-{suffix}",
            new(FileTransferProtocol.Smb, "synthetic-remote-host", null, "synthetic-remote-share", remote.Id, null));
        StorageTarget? second = null;
        if (withSecondTarget)
        {
            second = new StorageTarget(
                Guid.NewGuid(),
                $"target-b-{suffix}",
                new(FileTransferProtocol.Smb, "synthetic-remote-host-b", null, "synthetic-remote-share-b", remote.Id, null));
        }

        await using var db = database.CreateContext();
        db.AddRange(staging, sql, remote, server, instance, managed, target);
        if (second is not null)
        {
            db.Add(second);
        }

        await db.SaveChangesAsync();
        var definition = new BackupPlanDefinition(BackupPlanMode.Full,
            new(BackupScheduleType.Daily, TimeOnly.MinValue, BackupWeekdays.None),
            null, null, "UTC", BackupStorageMode.LocalAndRemote, target.Id, 7, 30, true, false, 120, 60, 60);
        var plan = BackupPlan.Create(Guid.NewGuid(), managed.Id, $"plan-{suffix}", Guid.NewGuid(), definition, DateTimeOffset.UtcNow);
        using var provider = database.CreateServiceProvider();
        await new BackupPlanStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()).AddAsync(plan);
        return new Graph(
            managed.Id,
            server.Id,
            staging.Id,
            target.Id,
            second?.Id,
            plan.Id,
            plan.CurrentVersion,
            plan.Name,
            instance.Id,
            instance.Name,
            server.Name,
            managed.DatabaseName,
            sql.Id);
    }

    private async Task<BackupFile[]> SeedHealthFilesAsync()
    {
        var graph = await SeedGraphAsync();
        foreach (var id in new[]
                 {
                     Guid.Parse("00000000-0000-0000-0000-000000000003"),
                     Guid.Parse("10000000-0000-0000-0000-000000000001"),
                     Guid.Parse("20000000-0000-0000-0000-000000000002"),
                 })
            await AddLocalFileAsync(graph, DateTimeOffset.UtcNow.AddDays(-1), 30, id);
        await using var db = database.CreateContext();
        var ordered = await db.BackupFiles.AsNoTracking().OrderBy(file => file.Id).ToArrayAsync();
        Assert.False(ordered.Select(file => file.Id).SequenceEqual(ordered.Select(file => file.Id).Order()));
        return ordered;
    }

    private async Task<BackupFile> AddLocalFileAsync(
        Graph graph, DateTimeOffset validatedAt, int days, Guid? id = null)
    {
        var fileId = id ?? Guid.NewGuid();
        return await AddFileAsync(
            graph,
            BackupFile.CreateLocal(
                fileId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                graph.DatabaseId,
                graph.ServerId,
                FileTransferProtocol.Smb,
                $@"\\synthetic-host\synthetic-share\{fileId:N}.bak",
                4096,
                validatedAt,
                days),
            validatedAt);
    }

    private async Task<BackupFile> AddRemoteFileAsync(
        Graph graph,
        Guid storageTargetId,
        DateTimeOffset validatedAt,
        int days)
    {
        var fileId = Guid.NewGuid();
        return await AddFileAsync(
            graph,
            BackupFile.CreateRemote(
                fileId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                graph.DatabaseId,
                storageTargetId,
                FileTransferProtocol.Smb,
                $@"\\synthetic-remote-host\synthetic-remote-share\{fileId:N}.bak",
                4096,
                validatedAt,
                days),
            validatedAt,
            storageTargetId);
    }

    private async Task<BackupFile> AddFileAsync(
        Graph graph,
        BackupFile file,
        DateTimeOffset validatedAt,
        Guid? storageTargetId = null)
    {
        var task = BackupTask.ForPlan(file.TaskId, graph.PlanId, graph.Version.Id, BackupRunPurpose.PlanFull,
            BackupTaskTriggerType.Manual, null);
        var snapshot = BackupTaskSnapshot.ForPlan(task, graph.Version, graph.PlanName, BackupRunPurpose.PlanFull,
            new(graph.ServerId, graph.ServerName, graph.InstanceId, graph.InstanceName, graph.DatabaseId, graph.DatabaseName),
            new("synthetic-sql", graph.SqlCredentialId, true, false, null, 15),
            new(@"D:\Synthetic", "v3", new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", graph.StagingCredentialId, null)),
            new(graph.StorageTargetId, new(FileTransferProtocol.Smb, "synthetic-remote-host", null, "synthetic-remote-share", graph.StagingCredentialId, null)));
        var attemptId = file.AttemptId.ToString("N");
        var attempt = new BackupAttempt(
            file.AttemptId,
            task.Id,
            1,
            validatedAt.AddMinutes(-2),
            new BackupAttemptPaths(
                $@"D:\Synthetic\{attemptId}.bak",
                $@"\\synthetic-host\synthetic-share\{attemptId}.bak",
                storageTargetId ?? graph.StorageTargetId,
                $@"\\synthetic-remote-host\synthetic-remote-share\{attemptId}.bak.part",
                $@"\\synthetic-remote-host\synthetic-remote-share\{attemptId}.bak"));
        attempt.MarkBackupRunning(validatedAt.AddMinutes(-1));
        attempt.RecordBackupSucceeded(validatedAt.AddSeconds(-30));
        attempt.RecordLocalVerification(file.LengthBytes, validatedAt);
        await using var db = database.CreateContext();
        db.AddRange(task, snapshot);
        await db.SaveChangesAsync();
        db.AddRange(
            attempt,
            file,
            new BackupFileStateChange(
                Guid.NewGuid(),
                file.Id,
                task.Id,
                null,
                BackupFileStatus.Available,
                "file.registered",
                validatedAt));
        await db.SaveChangesAsync();
        return file;
    }

    private sealed record Graph(
        Guid DatabaseId,
        Guid ServerId,
        Guid StagingCredentialId,
        Guid StorageTargetId,
        Guid? SecondStorageTargetId,
        Guid PlanId,
        BackupPlanVersion Version,
        string PlanName,
        Guid InstanceId,
        string InstanceName,
        string ServerName,
        string DatabaseName,
        Guid SqlCredentialId);

    private sealed class InterceptingFactory(
        PlatformDatabaseSqlServerFixture database,
        IInterceptor interceptor) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => database.CreateContext(interceptor);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class EndpointReadCounter : DbCommandInterceptor
    {
        public int EndpointReads;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[DatabaseServers]", StringComparison.Ordinal)
                || command.CommandText.Contains("[StorageTargets]", StringComparison.Ordinal))
                EndpointReads++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class HealthReadCounter : DbCommandInterceptor, IMaterializationInterceptor
    {
        public int FileReads;
        public int MaterializedFiles;

        public void Reset() => (FileReads, MaterializedFiles) = (0, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[BackupFiles]", StringComparison.Ordinal)) FileReads++;
            return ValueTask.FromResult(result);
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is BackupFile) MaterializedFiles++;
            return entity;
        }
    }

    private sealed class FakeRetentionFiles : IBackupFileStorageProbe, IBackupFileDeletionExecutor
    {
        public List<string> InspectedPaths { get; } = [];
        public HashSet<string> Present { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool WrongLength { get; init; }

        public bool RemoveOnDelete { get; set; }

        public BackupFileStorageOutcome DeleteOutcome { get; init; } = BackupFileStorageOutcome.Succeeded;

        public int Deletes;

        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint,
            string path,
            CancellationToken cancellationToken = default)
        {
            InspectedPaths.Add(path);
            if (!Present.Contains(path))
            {
                return Task.FromResult(BackupFileStorageResult.Succeeded(new BackupFileMetadata(false, false, null)));
            }

            return Task.FromResult(BackupFileStorageResult.Succeeded(
                new BackupFileMetadata(true, true, WrongLength ? 12 : 4096)));
        }

        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(
            BackupFileDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Deletes);
            if (RemoveOnDelete)
            {
                Present.Remove(request.Path);
            }

            if (DeleteOutcome == BackupFileStorageOutcome.Succeeded)
            {
                Present.Remove(request.Path);
                return Task.FromResult(BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance));
            }

            return Task.FromResult(BackupFileStorageResult.Indeterminate<BackupFileMutationReceipt>(
                BackupFileStorageFailureCode.ConnectionInterrupted,
                BackupFileStorageFailurePhase.Delete));
        }
    }
}
