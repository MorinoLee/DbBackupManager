using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>仅使用随机 LocalDB 库及测试临时目录，固定授权已提交但尚未调用 SQL 的中断窗口。</summary>
public sealed class BackupPlanE0401IndependentLocalDbTests(PlatformDatabaseSqlServerFixture platform, ITestOutputHelper output)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task SessionNumberAloneDoesNotMatchADifferentConnectionAndLoginTime()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var adapter = test.SqlAdapter();
        var prepared = await adapter.PrepareAsync(test.SqlInput,
            new(test.TargetDatabaseName, test.NewSqlPath(), BackupRunPurpose.PlanFull, true, false, 60), Guid.NewGuid());
        Assert.True(prepared.IsSucceeded);
        await using var invocation = prepared.Value!;
        var current = invocation.Binding;
        Assert.Equal(TargetSqlInvocationTerminationState.Active,
            (await adapter.ReadAsync(test.SqlInput, Guid.NewGuid(), current, 30)).State);

        // 仅验证真实 DMV 查询的身份匹配谓词，不声称本测试真的制造了 session_id 回收。
        var otherIdentity = current with
        {
            ConnectionId = Guid.NewGuid(),
            SessionEstablishedLocal = current.SessionEstablishedLocal!.Value.AddMinutes(-1)
        };
        var different = await adapter.ReadAsync(test.SqlInput, Guid.NewGuid(), otherIdentity, 30);
        Assert.Equal(current.SessionId, otherIdentity.SessionId);
        Assert.Equal(TargetSqlInvocationTerminationState.Terminated, different.State);
        Assert.Equal(0, test.Invocations);
        output.WriteLine("真实当前绑定=Active；同 SessionId 但 ConnectionId 与 LoginTime 均不同=Terminated；未实际制造会话号回收。");
    }

    [Fact]
    public async Task ClosedOriginalConnectionWithoutSuccessMetadataMustReachTerminationProbe()
    {
        var fault = new PlanExecutionCommitFault(nameof(IBackupInvocationAuthorizationStore.AuthorizeAsync));
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        await using var other = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var otherTaskId = await other.CreateTaskAsync(BackupRunPurpose.PlanFull);

        // 生产授权事务已真正提交；异常从其响应边界抛出，执行器的原连接随后正常释放。
        // 不声称这是进程击杀，也不伪造“已调用 BACKUP”事实。
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        Assert.True(fault.Fired);
        Assert.Equal(0, test.Invocations);

        Guid permitId;
        BackupInvocationBinding binding;
        string path;
        await using (var db = platform.CreateContext())
        {
            var authorization = await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId);
            Assert.Null(authorization.TerminalObservedAtUtc);
            Assert.Equal(BackupInvocationBindingState.Known, authorization.Binding.State);
            permitId = authorization.Id;
            binding = authorization.Binding;
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
            Assert.Equal(BackupInvocationStatus.Running, attempt.BackupInvocationStatus);
            path = attempt.LocalSqlFilePath;
        }
        Assert.False(File.Exists(path));
        await using (var sql = new SqlConnection("Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Pooling=false"))
        {
            await sql.OpenAsync();
            await using var command = new SqlCommand("SELECT COUNT(*) FROM msdb.dbo.backupset WHERE database_name=@database", sql);
            command.Parameters.AddWithValue("@database", test.TargetDatabaseName);
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        var direct = await test.SqlAdapter().ReadAsync(test.SqlInput, permitId, binding, 30);
        Assert.Equal(TargetSqlInvocationTerminationState.Terminated, direct.State);
        Assert.Equal("termination.original_connection_closed", direct.ReasonCode);
        Assert.Equal(BackupExecutionContractCode.Conflict, await other.Runner.RunTaskOnceAsync(new(otherTaskId, Guid.NewGuid())));

        for (var round = 0; round < 3; round++)
        {
            await test.ExpireLeaseAsync(taskId);
            await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        }
        string? reason;
        bool unterminated;
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            reason = task.ErrorCode;
            unterminated = (await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId)).TerminalObservedAtUtc is null;
            Assert.Single(await db.BackupAttempts.Where(x => x.TaskId == taskId).ToArrayAsync());
        }
        var otherResult = await other.Runner.RunTaskOnceAsync(new(otherTaskId, Guid.NewGuid()));
        output.WriteLine($"DirectProbe={direct.State}; MissingFile={!File.Exists(path)}; BackupInvocations={test.Invocations}; MetadataReads={test.MetadataReads}; RecoveryTerminationReads={test.TerminationReads}; Unterminated={unterminated}; Reason={reason}; OtherDatabaseResult={otherResult}");

        Assert.True(test.TerminationReads > 0,
            $"原连接由真实适配器证实已终止、无历史/文件，但恢复未进入终止探针：reads={test.TerminationReads}; reason={reason}; other={otherResult}。");
    }
}
