using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupTaskPersistenceSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task TaskSnapshotAttemptAndStateHistoryRoundTrip()
    {
        var graph = await AddConfigurationGraphAsync();
        var now = DateTimeOffset.UtcNow;
        var task = new BackupTask(
            Guid.NewGuid(),
            graph.PolicyId,
            BackupTaskTriggerType.Scheduled,
            now);
        var snapshot = CreateSnapshot(task.Id, graph);
        var initialChange = CreateInitialChange(task.Id, Guid.NewGuid(), now);

        await using (var setup = database.CreateContext())
        {
            setup.AddRange(task, snapshot, initialChange);
            await setup.SaveChangesAsync();
        }

        var attemptId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        await using (var claim = database.CreateContext())
        {
            var storedTask = await claim.BackupTasks.SingleAsync(x => x.Id == task.Id);
            var attempt = CreateAttempt(storedTask.Id, attemptId, graph.StorageTargetId, now);
            storedTask.ClaimExecution(
                BackupStorageMode.LocalAndRemote,
                attempt.Id,
                leaseToken,
                "worker-test",
                now.AddSeconds(1),
                now.AddMinutes(5));
            claim.BackupAttempts.Add(attempt);
            claim.BackupTaskStateChanges.Add(new BackupTaskStateChange(
                Guid.NewGuid(),
                storedTask.Id,
                BackupTaskStatus.Pending,
                BackupTaskStage.Backup,
                BackupTaskStatus.Running,
                BackupTaskStage.Backup,
                attempt.Id,
                "execution.claimed",
                null,
                now.AddSeconds(1)));
            await claim.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();
        var persistedTask = await verify.BackupTasks.AsNoTracking().SingleAsync(x => x.Id == task.Id);
        var persistedSnapshot = await verify.BackupTaskSnapshots
            .AsNoTracking()
            .SingleAsync(x => x.TaskId == task.Id);
        var persistedAttempt = await verify.BackupAttempts
            .AsNoTracking()
            .SingleAsync(x => x.Id == attemptId);
        var history = await verify.BackupTaskStateChanges
            .AsNoTracking()
            .Where(x => x.TaskId == task.Id)
            .OrderBy(x => x.Id)
            .ToListAsync();

        Assert.Equal(BackupTaskStatus.Running, persistedTask.Status);
        Assert.Equal(attemptId, persistedTask.CurrentBackupAttemptId);
        Assert.Equal(leaseToken, persistedTask.LeaseToken);
        Assert.Equal(graph.PolicyName, persistedSnapshot.PolicyName);
        Assert.Equal(BackupStorageMode.LocalAndRemote, persistedSnapshot.StorageMode);
        Assert.Equal(BackupInvocationStatus.Prepared, persistedAttempt.BackupInvocationStatus);
        Assert.NotEmpty(persistedTask.RowVersion);
        Assert.NotEmpty(persistedAttempt.RowVersion);
        Assert.Collection(
            history,
            change => Assert.Equal("task.created", change.ReasonCode),
            change => Assert.Equal("execution.claimed", change.ReasonCode));
    }

    [Fact]
    public async Task ScheduledSlotAndMutationIdAreUniqueWhileManualTasksCanRepeat()
    {
        var graph = await AddConfigurationGraphAsync();
        var slot = DateTimeOffset.UtcNow;
        var first = new BackupTask(
            Guid.NewGuid(),
            graph.PolicyId,
            BackupTaskTriggerType.Scheduled,
            slot);
        var mutationId = Guid.NewGuid();

        await using (var setup = database.CreateContext())
        {
            setup.Add(first);
            setup.Add(CreateInitialChange(first.Id, mutationId, slot));
            await setup.SaveChangesAsync();
        }

        await using (var duplicateSlot = database.CreateContext())
        {
            duplicateSlot.Add(new BackupTask(
                Guid.NewGuid(),
                graph.PolicyId,
                BackupTaskTriggerType.Scheduled,
                slot));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateSlot.SaveChangesAsync());
        }

        await using (var duplicateMutation = database.CreateContext())
        {
            duplicateMutation.Add(CreateInitialChange(first.Id, mutationId, slot.AddSeconds(1)));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateMutation.SaveChangesAsync());
        }

        await using var manual = database.CreateContext();
        manual.AddRange(
            new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null),
            new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null));
        await manual.SaveChangesAsync();
    }

    [Fact]
    public async Task CompositeCurrentAttemptForeignKeyRejectsAttemptFromAnotherTask()
    {
        var graph = await AddConfigurationGraphAsync();
        var now = DateTimeOffset.UtcNow;
        var ownerTask = new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null);
        var otherTask = new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null);
        var attempt = CreateAttempt(ownerTask.Id, Guid.NewGuid(), graph.StorageTargetId, now);

        await using (var setup = database.CreateContext())
        {
            setup.AddRange(ownerTask, otherTask);
            await setup.SaveChangesAsync();
            setup.Add(attempt);
            await setup.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupTasks] SET [CurrentBackupAttemptId] = {attempt.Id} WHERE [Id] = {otherTask.Id}"));

        Assert.Equal(547, exception.Number);
    }

    [Fact]
    public async Task DatabaseRejectsIncompleteRunningLease()
    {
        var graph = await AddConfigurationGraphAsync();
        var task = new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null);

        await using (var setup = database.CreateContext())
        {
            setup.Add(task);
            await setup.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupTasks] SET [Status] = 'Running' WHERE [Id] = {task.Id}"));

        Assert.Equal(547, exception.Number);
    }

    [Fact]
    public async Task RowVersionRejectsStaleTaskUpdate()
    {
        var graph = await AddConfigurationGraphAsync();
        var task = new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null);

        await using (var setup = database.CreateContext())
        {
            setup.Add(task);
            await setup.SaveChangesAsync();
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var firstCopy = await first.BackupTasks.SingleAsync(x => x.Id == task.Id);
        var staleCopy = await second.BackupTasks.SingleAsync(x => x.Id == task.Id);
        var now = DateTimeOffset.UtcNow;

        firstCopy.RequestCancellation(now, BackupStorageMode.LocalAndRemote);
        await first.SaveChangesAsync();
        staleCopy.RequestCancellation(now.AddSeconds(1), BackupStorageMode.LocalAndRemote);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task RowVersionRejectsStaleAttemptUpdate()
    {
        var graph = await AddConfigurationGraphAsync();
        var now = DateTimeOffset.UtcNow;
        var task = new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null);
        var attempt = CreateAttempt(task.Id, Guid.NewGuid(), graph.StorageTargetId, now);

        await using (var setup = database.CreateContext())
        {
            setup.Add(task);
            await setup.SaveChangesAsync();
            setup.Add(attempt);
            await setup.SaveChangesAsync();
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var firstCopy = await first.BackupAttempts.SingleAsync(x => x.Id == attempt.Id);
        var staleCopy = await second.BackupAttempts.SingleAsync(x => x.Id == attempt.Id);

        firstCopy.MarkBackupRunning(now.AddSeconds(1));
        await first.SaveChangesAsync();
        staleCopy.MarkBackupRunning(now.AddSeconds(2));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task AttemptIdentityPathsAndExistingEvidenceCannotBeOverwritten()
    {
        var graph = await AddConfigurationGraphAsync();
        var now = DateTimeOffset.UtcNow;
        var task = new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null);
        var attempt = CreateAttempt(task.Id, Guid.NewGuid(), graph.StorageTargetId, now);
        attempt.MarkBackupRunning(now.AddSeconds(1));
        attempt.RecordBackupSucceeded(now.AddSeconds(2));
        attempt.RecordLocalVerification(1024, now.AddSeconds(3));

        await using (var setup = database.CreateContext())
        {
            setup.Add(task);
            await setup.SaveChangesAsync();
            setup.Add(attempt);
            await setup.SaveChangesAsync();
        }

        await using (var pathContext = database.CreateContext())
        {
            var stored = await pathContext.BackupAttempts.SingleAsync(x => x.Id == attempt.Id);
            pathContext.Entry(stored).Property(x => x.LocalSqlFilePath).CurrentValue =
                $@"D:\Synthetic\SqlBackup\tampered-{attempt.Id:N}.bak";
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => pathContext.SaveChangesAsync());
            Assert.Contains("路径", exception.Message, StringComparison.Ordinal);
        }

        await using (var evidenceContext = database.CreateContext())
        {
            var stored = await evidenceContext.BackupAttempts.SingleAsync(x => x.Id == attempt.Id);
            evidenceContext.Entry(stored).Property(x => x.SourceLengthBytes).CurrentValue = 2048;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => evidenceContext.SaveChangesAsync());
            Assert.Contains("证据", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BackupFileIdentityConstraintsAndRowVersionProtectDeletionLease()
    {
        var graph = await AddConfigurationGraphAsync();
        var now = DateTimeOffset.UtcNow;
        var task = new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null);
        var snapshot = CreateSnapshot(task.Id, graph);
        var attempt = CreateAttempt(task.Id, Guid.NewGuid(), graph.StorageTargetId, now);
        attempt.MarkBackupRunning(now.AddSeconds(1));
        attempt.RecordBackupSucceeded(now.AddSeconds(2));
        attempt.RecordLocalVerification(4096, now.AddSeconds(3));
        var sourceLength = attempt.SourceLengthBytes.GetValueOrDefault();
        var verifiedAt = attempt.LocalVerifiedAtUtc.GetValueOrDefault();
        Assert.True(sourceLength > 0);
        Assert.NotEqual(default, verifiedAt);
        var file = BackupFile.CreateLocal(
            Guid.NewGuid(),
            task.Id,
            attempt.Id,
            graph.DatabaseId,
            graph.ServerId,
            FileTransferProtocol.Smb,
            attempt.WorkerSourceFilePath,
            sourceLength,
            verifiedAt,
            7);
        var registeredMutation = Guid.NewGuid();

        await using (var setup = database.CreateContext())
        {
            setup.AddRange(task, snapshot);
            await setup.SaveChangesAsync();
            setup.AddRange(
                attempt,
                file,
                new BackupFileStateChange(
                    registeredMutation,
                    file.Id,
                    task.Id,
                    null,
                    BackupFileStatus.Available,
                    "file.registered",
                    now.AddSeconds(3)));
            await setup.SaveChangesAsync();
        }

        await using (var immutable = database.CreateContext())
        {
            var stored = await immutable.BackupFiles.SingleAsync(item => item.Id == file.Id);
            immutable.Entry(stored).Property(item => item.Path).CurrentValue =
                $@"\\synthetic-smb-host\synthetic-share\tampered-{file.Id:N}.bak";
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => immutable.SaveChangesAsync());
            Assert.Contains("身份", exception.Message, StringComparison.Ordinal);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var firstCopy = await first.BackupFiles.SingleAsync(item => item.Id == file.Id);
        var staleCopy = await second.BackupFiles.SingleAsync(item => item.Id == file.Id);
        var acquiredAt = now.AddDays(8);
        firstCopy.ClaimDeletion(Guid.NewGuid(), "retention-a", acquiredAt, acquiredAt.AddMinutes(5));
        staleCopy.ClaimDeletion(Guid.NewGuid(), "retention-b", acquiredAt, acquiredAt.AddMinutes(5));
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var invalidIdentity = database.CreateContext();
        var databaseException = await Assert.ThrowsAsync<SqlException>(() =>
            invalidIdentity.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupFiles] SET [StorageTargetId] = {graph.StorageTargetId} WHERE [Id] = {file.Id}"));
        Assert.Equal(547, databaseException.Number);
    }

    [Fact]
    public async Task SnapshotAndStateHistoryCannotBeChangedAndTaskCannotBeDeleted()
    {
        var graph = await AddConfigurationGraphAsync();
        var now = DateTimeOffset.UtcNow;
        var task = new BackupTask(Guid.NewGuid(), graph.PolicyId, BackupTaskTriggerType.Manual, null);
        var snapshot = CreateSnapshot(task.Id, graph);
        var change = CreateInitialChange(task.Id, Guid.NewGuid(), now);

        await using (var setup = database.CreateContext())
        {
            setup.AddRange(task, snapshot, change);
            await setup.SaveChangesAsync();
        }

        await using (var snapshotContext = database.CreateContext())
        {
            var stored = await snapshotContext.BackupTaskSnapshots.SingleAsync(x => x.TaskId == task.Id);
            snapshotContext.Entry(stored).Property(x => x.PolicyName).CurrentValue = "被篡改";
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => snapshotContext.SaveChangesAsync());
            Assert.Contains("快照", exception.Message, StringComparison.Ordinal);
        }

        await using (var historyContext = database.CreateContext())
        {
            var stored = await historyContext.BackupTaskStateChanges.SingleAsync(x => x.TaskId == task.Id);
            historyContext.Entry(stored).Property(x => x.ReasonCode).CurrentValue = "tampered";
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => historyContext.SaveChangesAsync());
            Assert.Contains("只能追加", exception.Message, StringComparison.Ordinal);
        }

        await using var deleteContext = database.CreateContext();
        var storedTask = await deleteContext.BackupTasks.SingleAsync(x => x.Id == task.Id);
        deleteContext.Remove(storedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deleteContext.SaveChangesAsync());
    }

    private async Task<ConfigurationGraph> AddConfigurationGraphAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stagingCredential = new CredentialReference(
            Guid.NewGuid(),
            $"SyntheticStagingCredential-{suffix}",
            CredentialKind.SmbPassword,
            "synthetic-file-user",
            "protected:synthetic-staging-secret",
            "dp-v1");
        var sqlCredential = new CredentialReference(
            Guid.NewGuid(),
            $"SyntheticSqlCredential-{suffix}",
            CredentialKind.SqlPassword,
            "synthetic-sql-user",
            "protected:synthetic-sql-secret",
            "dp-v1");
        var remoteCredential = new CredentialReference(
            Guid.NewGuid(),
            $"SyntheticRemoteCredential-{suffix}",
            CredentialKind.SftpPassword,
            "synthetic-transfer-user",
            "protected:synthetic-transfer-secret",
            "dp-v1");
        var server = new DatabaseServer(
            Guid.NewGuid(),
            $"SyntheticServer-{suffix}",
            @"D:\Synthetic\SqlBackup",
            new FileEndpointSettings(
                FileTransferProtocol.Smb,
                "synthetic-smb-host",
                null,
                "synthetic-share",
                stagingCredential.Id,
                null));
        var instance = new DatabaseInstance(
            Guid.NewGuid(),
            server.Id,
            $"SyntheticInstance-{suffix}",
            $"synthetic-sql-host-{suffix}",
            sqlCredential.Id,
            encryptConnection: true,
            trustServerCertificate: false,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 30);
        var managedDatabase = new ManagedDatabase(
            Guid.NewGuid(),
            instance.Id,
            $"SyntheticDatabase_{suffix}",
            isSystemDatabase: false,
            isAvailable: true,
            DateTimeOffset.UtcNow,
            "FULL",
            "ONLINE");
        managedDatabase.SetManaged(true);
        var remoteEndpoint = new FileEndpointSettings(
            FileTransferProtocol.Sftp,
            "synthetic-sftp-host",
            22,
            "/synthetic/root",
            remoteCredential.Id,
            "SHA256:synthetic-host-key");
        var storageTarget = new StorageTarget(
            Guid.NewGuid(),
            $"SyntheticStorage-{suffix}",
            remoteEndpoint);
        var policyName = $"SyntheticPolicy-{suffix}";
        var policy = new BackupPolicy(
            Guid.NewGuid(),
            policyName,
            managedDatabase.Id,
            new BackupPolicySettings(
                BackupStorageMode.LocalAndRemote,
                storageTarget.Id,
                BackupScheduleType.Daily,
                new TimeOnly(2, 0),
                BackupWeekdays.None,
                "Taipei Standard Time",
                LocalRetentionDays: 7,
                RemoteRetentionDays: 30,
                UseChecksum: true,
                UseCompression: true,
                UseCopyOnly: false,
                BackupTimeoutMinutes: 120,
                VerifyTimeoutMinutes: 60,
                TransferTimeoutMinutes: 180),
            isEnabled: true,
            nowUtc: DateTimeOffset.UtcNow);

        await using var context = database.CreateContext();
        context.AddRange(
            stagingCredential,
            sqlCredential,
            remoteCredential,
            server,
            instance,
            managedDatabase,
            storageTarget,
            policy);
        await context.SaveChangesAsync();

        return new ConfigurationGraph(
            policy.Id,
            policyName,
            server.Id,
            server.Name,
            instance.Id,
            instance.Name,
            instance.ConnectionAddress,
            sqlCredential.Id,
            managedDatabase.Id,
            managedDatabase.DatabaseName,
            stagingCredential.Id,
            storageTarget.Id,
            remoteEndpoint);
    }

    private static BackupTaskSnapshot CreateSnapshot(Guid taskId, ConfigurationGraph graph)
    {
        return new BackupTaskSnapshot(
            taskId,
            graph.PolicyName,
            new BackupTaskIdentitySnapshot(
                graph.ServerId,
                graph.ServerName,
                graph.InstanceId,
                graph.InstanceName,
                graph.DatabaseId,
                graph.DatabaseName),
            new BackupSqlTargetSnapshot(
                graph.ConnectionAddress,
                graph.SqlCredentialId,
                EncryptConnection: true,
                TrustServerCertificate: false,
                CertificateTrustReason: null,
                ConnectionTimeoutSeconds: 30),
            new BackupSourceSnapshot(
                @"D:\Synthetic\SqlBackup",
                "v1",
                new FileEndpointSettings(
                    FileTransferProtocol.Smb,
                    "synthetic-smb-host",
                    null,
                    "synthetic-share",
                    graph.StagingCredentialId,
                    null)),
            new BackupTaskPolicySnapshot(
                BackupStorageMode.LocalAndRemote,
                new BackupRemoteTargetSnapshot(graph.StorageTargetId, graph.RemoteEndpoint),
                LocalRetentionDays: 7,
                RemoteRetentionDays: 30,
                UseChecksum: true,
                UseCompression: true,
                UseCopyOnly: false,
                BackupTimeoutMinutes: 120,
                VerifyTimeoutMinutes: 60,
                TransferTimeoutMinutes: 180,
                TimeZoneId: "Taipei Standard Time"));
    }

    private static BackupAttempt CreateAttempt(
        Guid taskId,
        Guid attemptId,
        Guid storageTargetId,
        DateTimeOffset preparedAtUtc)
    {
        var id = attemptId.ToString("N");
        return new BackupAttempt(
            attemptId,
            taskId,
            1,
            preparedAtUtc,
            new BackupAttemptPaths(
                $@"D:\Synthetic\SqlBackup\{id}.bak",
                $@"\\synthetic-smb-host\synthetic-share\{id}.bak",
                storageTargetId,
                $"/synthetic/root/{id}.bak.part",
                $"/synthetic/root/{id}.bak"));
    }

    private static BackupTaskStateChange CreateInitialChange(
        Guid taskId,
        Guid mutationId,
        DateTimeOffset occurredAtUtc)
    {
        return new BackupTaskStateChange(
            mutationId,
            taskId,
            fromStatus: null,
            fromStage: null,
            BackupTaskStatus.Pending,
            BackupTaskStage.Backup,
            backupAttemptId: null,
            "task.created",
            null,
            occurredAtUtc);
    }

    private sealed record ConfigurationGraph(
        Guid PolicyId,
        string PolicyName,
        Guid ServerId,
        string ServerName,
        Guid InstanceId,
        string InstanceName,
        string ConnectionAddress,
        Guid SqlCredentialId,
        Guid DatabaseId,
        string DatabaseName,
        Guid StagingCredentialId,
        Guid StorageTargetId,
        FileEndpointSettings RemoteEndpoint);
}
