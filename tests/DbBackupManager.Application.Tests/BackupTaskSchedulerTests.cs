using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupTaskSchedulerTests
{
    [Fact]
    public async Task IsolatedPolicyFailureDoesNotStopRemainingPolicies()
    {
        var due = new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.FromHours(8));
        var now = due.AddHours(1);
        var bad = Policy(Guid.NewGuid(), "Synthetic/Unknown-Zone", due.AddDays(-1));
        var good = Policy(Guid.NewGuid(), "Taipei Standard Time", due.AddDays(-1));
        var store = new FakeStore { CreatedId = Guid.NewGuid() };
        var scheduler = new BackupTaskScheduler(
            new FakeReader([bad, good]),
            store,
            new FrozenTime(now));

        var result = await scheduler.RunOnceAsync();

        Assert.Equal(2, result.Policies.Count);
        Assert.Equal(BackupSchedulePolicyResultCode.InvalidTimeZone, result.Policies[0].Code);
        Assert.Equal(BackupSchedulePolicyResultCode.Created, result.Policies[1].Code);
        Assert.Equal(good.PolicyId, store.Commands.Single().PolicyId);
        Assert.Equal(due.ToUniversalTime(), store.Commands.Single().ScheduledSlotAtUtc);
    }

    [Fact]
    public async Task NotDueAndAlreadyExistsReturnStableResults()
    {
        var due = new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.FromHours(8));
        var notDue = Policy(Guid.NewGuid(), "Taipei Standard Time", due.AddHours(3));
        var existing = Policy(Guid.NewGuid(), "Taipei Standard Time", due.AddDays(-1));
        var store = new FakeStore { Code = BackupTaskStoreResultCode.AlreadyExists, CreatedId = Guid.NewGuid() };
        var scheduler = new BackupTaskScheduler(
            new FakeReader([notDue, existing]),
            store,
            new FrozenTime(due.AddHours(1)));

        var result = await scheduler.RunOnceAsync();

        Assert.Equal(BackupSchedulePolicyResultCode.NotDue, result.Policies[0].Code);
        Assert.Equal(BackupSchedulePolicyResultCode.AlreadyExists, result.Policies[1].Code);
        Assert.Single(store.Commands);
    }

    [Fact]
    public async Task ConfigurationUnavailableAndCreateExceptionAreIsolated()
    {
        var due = new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.FromHours(8));
        var first = Policy(Guid.NewGuid(), "Taipei Standard Time", due.AddDays(-1));
        var second = Policy(Guid.NewGuid(), "Taipei Standard Time", due.AddDays(-1));
        var store = new FakeStore { ThrowOn = first.PolicyId, Code = BackupTaskStoreResultCode.ConfigurationUnavailable };
        var scheduler = new BackupTaskScheduler(
            new FakeReader([first, second]),
            store,
            new FrozenTime(due.AddHours(1)));

        var result = await scheduler.RunOnceAsync();

        Assert.Equal(BackupSchedulePolicyResultCode.CreateFailed, result.Policies[0].Code);
        Assert.Equal(BackupSchedulePolicyResultCode.ConfigurationUnavailable, result.Policies[1].Code);
    }

    [Fact]
    public async Task ReaderFailurePropagatesToCaller()
    {
        var scheduler = new BackupTaskScheduler(new ThrowingReader(), new FakeStore(), new FrozenTime(DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunOnceAsync());
    }

    [Fact]
    public async Task CreateTaskConnectionFailurePropagatesToCaller()
    {
        var due = new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.FromHours(8));
        var first = Policy(Guid.NewGuid(), "Taipei Standard Time", due.AddDays(-1));
        var second = Policy(Guid.NewGuid(), "Taipei Standard Time", due.AddDays(-1));
        var store = new FakeStore { ThrowDbOn = first.PolicyId };
        var scheduler = new BackupTaskScheduler(
            new FakeReader([first, second]),
            store,
            new FrozenTime(due.AddHours(1)));

        var exception = await Assert.ThrowsAsync<FakeDbException>(() => scheduler.RunOnceAsync());
        Assert.Equal("platform-db", exception.Message);
        Assert.Empty(store.Commands);
    }

    private static SchedulableBackupPolicy Policy(Guid id, string timeZone, DateTimeOffset effective) =>
        new(id, BackupScheduleType.Daily, new TimeOnly(2, 0), BackupWeekdays.None, timeZone, effective);

    private sealed class FrozenTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeReader(IReadOnlyList<SchedulableBackupPolicy> policies) : ISchedulableBackupPolicyReader
    {
        public Task<IReadOnlyList<SchedulableBackupPolicy>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(policies);
    }

    private sealed class ThrowingReader : ISchedulableBackupPolicyReader
    {
        public Task<IReadOnlyList<SchedulableBackupPolicy>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("platform-db");
    }

    private sealed class FakeStore : IBackupTaskExecutionStore
    {
        public BackupTaskStoreResultCode Code { get; set; } = BackupTaskStoreResultCode.Succeeded;
        public Guid CreatedId { get; set; }
        public Guid? ThrowOn { get; set; }
        public Guid? ThrowDbOn { get; set; }
        public List<CreateBackupTaskCommand> Commands { get; } = [];

        public Task<BackupTaskStoreResult<BackupTaskStateModel>> CreateTaskAsync(
            CreateBackupTaskCommand command,
            CancellationToken cancellationToken = default)
        {
            if (ThrowDbOn == command.PolicyId)
            {
                throw new FakeDbException();
            }

            if (ThrowOn == command.PolicyId)
            {
                throw new InvalidOperationException("create-failed");
            }

            Commands.Add(command);
            var task = new BackupTaskStateModel(
                CreatedId == Guid.Empty ? command.TaskId : CreatedId,
                BackupTaskStatus.Pending,
                null,
                null,
                null,
                0,
                null,
                null,
                null,
                null);
            return Task.FromResult(new BackupTaskStoreResult<BackupTaskStateModel>(Code, task));
        }

        public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimNextAsync(ClaimNextBackupTaskCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimTaskAsync(Guid taskId, ClaimNextBackupTaskCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<LeaseHandle>> MarkBackupInvocationStartedAsync(LeaseHandle lease, byte[] attemptRowVersion, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<LeaseHandle>> RenewLeaseAsync(LeaseHandle lease, DateTimeOffset utcNow, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> RefreshLeaseWorkItemAsync(Guid taskId, Guid leaseToken, BackupLeasePurpose purpose, DateTimeOffset utcNow, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupTaskTransitionModel>> CommitStageAsync(LeaseHandle lease, BackupStageCommitCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestCancellationAsync(BackupTaskMutationCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupTaskStateModel>> RetryFailedAsync(BackupTaskMutationCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestReconciliationAsync(BackupTaskMutationCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> FindExpiredExecutionTaskIdsAsync(DateTimeOffset utcNow, int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupTaskStateModel>> ExpireExecutionLeaseAsync(ExpireExecutionLeaseCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> FindReconciliationCandidateTaskIdsAsync(DateTimeOffset utcNow, int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> AcquireReconciliationLeaseAsync(AcquireReconciliationLeaseCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupTaskStateModel>> CommitReconciliationAsync(LeaseHandle lease, ReconciliationCommitCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupTaskStateModel>> ConfirmNeedsAttentionAsync(ConfirmNeedsAttentionCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStateModel?> FindTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeDbException() : DbException("platform-db");
}
