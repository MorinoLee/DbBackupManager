using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Application.Tests;

public sealed class BackupPlanWorkerRoundTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnePlanFailureDoesNotStopTheRoundAndOnlyOneTaskRuns()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var scheduling = new SchedulingFake([first, second], first);
        var runner = new RunnerFake();
        var round = Round(scheduling, new QueueFake(new(BackupPlanWorkerSelectionKind.Execute, taskId)), runner, new ReconcilerFake());

        var result = await round.RunOnceAsync();

        Assert.Equal(2, result.PlansConsidered);
        Assert.Equal(1, result.ScheduleFailures);
        Assert.Equal([second], scheduling.Scheduled);
        Assert.Equal(BackupPlanWorkerAction.Executed, result.Action);
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal(BackupExecutionContractCode.Succeeded, result.Code);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task UnknownResultIsReconciledAndDoesNotCallTheRunner()
    {
        var taskId = Guid.NewGuid();
        var runner = new RunnerFake();
        var reconciler = new ReconcilerFake();
        var round = Round(new SchedulingFake([Guid.NewGuid()]), new QueueFake(new(BackupPlanWorkerSelectionKind.Reconcile, taskId)), runner, reconciler);

        var result = await round.RunOnceAsync();

        Assert.Equal(BackupPlanWorkerAction.Reconciled, result.Action);
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal(BackupExecutionContractCode.Succeeded, result.Code);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(1, reconciler.Calls);
    }

    [Fact]
    public async Task RunnerFailureStaysInsideTheRound()
    {
        var runner = new RunnerFake { Throw = true };
        var round = Round(
            new SchedulingFake([]),
            new QueueFake(new(BackupPlanWorkerSelectionKind.Execute, Guid.NewGuid())),
            runner,
            new ReconcilerFake());

        var result = await round.RunOnceAsync();

        Assert.Equal(BackupPlanWorkerAction.Executed, result.Action);
        Assert.Null(result.Code);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var round = Round(new SchedulingFake([Guid.NewGuid()]), new QueueFake(new(BackupPlanWorkerSelectionKind.Idle, null)), new RunnerFake(), new ReconcilerFake());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => round.RunOnceAsync(cancellation.Token));
    }

    private static BackupPlanWorkerRound Round(
        SchedulingFake scheduling, QueueFake queue, RunnerFake runner, ReconcilerFake reconciler) =>
        new(scheduling, queue, runner, reconciler, new FrozenTime());

    private sealed class FrozenTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SchedulingFake(IReadOnlyList<Guid> ids, Guid? failing = null) : IBackupPlanSchedulingStore
    {
        public List<Guid> Scheduled { get; } = [];

        public Task<IReadOnlyList<Guid>> ReadPlanIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ids);

        public Task<BackupPlanSchedulingResult> ScheduleAsync(
            Guid planId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (planId == failing)
            {
                throw new InvalidOperationException("合成计划调度失败。");
            }

            Scheduled.Add(planId);
            return Task.FromResult(new BackupPlanSchedulingResult(planId, BackupPlanSchedulingCode.NoWork, []));
        }
    }

    private sealed class QueueFake(BackupPlanWorkerSelection selection) : IBackupPlanWorkerStore
    {
        public Task<BackupPlanWorkerSelection> SelectAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(selection);
    }

    private sealed class RunnerFake : IBackupPlanTaskRunner
    {
        public int Calls { get; private set; }

        public bool Throw { get; init; }

        public Task<BackupExecutionContractCode> RunTaskOnceAsync(
            RunBackupPlanTaskRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw)
            {
                throw new InvalidOperationException("合成执行失败。");
            }

            return Task.FromResult(BackupExecutionContractCode.Succeeded);
        }
    }

    private sealed class ReconcilerFake : IBackupPlanTaskReconciler
    {
        public int Calls { get; private set; }

        public Task<BackupExecutionContractCode> ReconcilePlanTaskOnceAsync(
            ReconcileBackupPlanTaskRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(BackupExecutionContractCode.Succeeded);
        }
    }
}
