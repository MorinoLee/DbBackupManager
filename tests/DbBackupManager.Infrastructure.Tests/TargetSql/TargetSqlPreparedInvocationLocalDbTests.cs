using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlPreparedInvocationLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task RealSessionIsBoundConsumedOnceAndTerminationRequiresItsActualClosure()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var adapter = test.SqlAdapter();
        var permit = Guid.NewGuid();
        var prepared = await adapter.PrepareAsync(test.SqlInput,
            new(test.TargetDatabaseName, test.NewSqlPath(), BackupRunPurpose.PlanFull, true, false, 60), Guid.NewGuid());
        Assert.True(prepared.IsSucceeded);
        await using var invocation = prepared.Value!;
        Assert.NotNull(invocation.Database.DatabaseGuid);
        Assert.NotNull(invocation.Database.FamilyGuid);
        var active = await adapter.ReadAsync(test.SqlInput, permit, invocation.Binding, 30);
        Assert.True(active.State == TargetSqlInvocationTerminationState.Active, active.ReasonCode);
        Assert.True((await invocation.InvokeOnceAsync(permit)).IsSucceeded);
        Assert.Equal(TargetSqlFailureCode.CommandRejected, (await invocation.InvokeOnceAsync(permit)).Failure!.Code);
        await invocation.DisposeAsync();
        Assert.Equal(TargetSqlInvocationTerminationState.Terminated,
            (await adapter.ReadAsync(test.SqlInput, permit, invocation.Binding, 30)).State);
    }

    [Fact]
    public async Task UnvalidatedDifferentialVersionNeverGetsAnInvocationOrBackupFile()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var path = test.NewSqlPath();
        var prepared = await test.SqlAdapter(false).PrepareAsync(test.SqlInput,
            new(test.TargetDatabaseName, path, BackupRunPurpose.PlanDifferential, true, false, 60), Guid.NewGuid());
        Assert.False(prepared.IsSucceeded);
        Assert.Equal(TargetSqlFailureCode.BackupTypeNotValidated, prepared.Failure!.Code);
        Assert.Null(prepared.Value);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ExistingAttemptPathAppearingAfterPreparationIsRejectedWithoutAppending()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var path = test.NewSqlPath();
        var prepared = await test.SqlAdapter().PrepareAsync(test.SqlInput,
            new(test.TargetDatabaseName, path, BackupRunPurpose.PlanFull, true, false, 60), Guid.NewGuid());
        Assert.True(prepared.IsSucceeded);
        await using var invocation = prepared.Value!;
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, bytes);
        var response = await invocation.InvokeOnceAsync(Guid.NewGuid());
        Assert.False(response.IsSucceeded);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }
}
