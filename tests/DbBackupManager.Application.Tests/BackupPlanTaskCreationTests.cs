using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupPlanTaskCreationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EntrypointsFixTriggerActorAndClockWithoutExposingASnapshot()
    {
        var port = new RecordingStore();
        var useCase = new BackupPlanTaskCreationService(port, new FrozenTime());
        var request = Request(Plan());
        var actor = new AdminSession(Guid.NewGuid(), "合成管理员", "synthetic-stamp");
        using var cancellation = new CancellationTokenSource();
        await useCase.CreateManualAsync(request, actor, cancellation.Token);
        Assert.Equal(new(request, BackupTaskTriggerType.Manual, Now, actor.AdminUserId, actor.SecurityStamp), port.Command);
        Assert.Equal(cancellation.Token, port.Token);
        await useCase.CreateScheduledAsync(request with { ScheduledSlotAtUtc = Now.AddHours(-2) });
        Assert.Equal(BackupTaskTriggerType.Scheduled, port.Command!.TriggerType);
        Assert.Null(port.Command.ActorAdminUserId);
        Assert.Null(port.Command.ActorSecurityStamp);
        Assert.Equal(Now, port.Command.NowUtc);
    }

    [Theory]
    [InlineData(BackupRunPurpose.PlanFull)]
    [InlineData(BackupRunPurpose.PlanDifferential)]
    [InlineData(BackupRunPurpose.AdHocCopyOnlyFull)]
    public void ManualPurposeIsAcceptedEvenWhenPaused(BackupRunPurpose purpose)
    {
        var plan = Plan();
        plan.Pause();
        Assert.Null(Validate(plan, Request(plan) with { Purpose = purpose }));
    }

    [Fact]
    public void UnsupportedLogHasExplicitStageResult()
    {
        var plan = Plan();
        Assert.Equal(BackupPlanTaskCreationCode.StageNotOpen, Validate(plan, Request(plan) with { Purpose = BackupRunPurpose.PlanLog }));
    }

    [Theory]
    [InlineData("wrong_plan", BackupPlanTaskCreationCode.VersionConflict)]
    [InlineData("wrong_version", BackupPlanTaskCreationCode.VersionConflict)]
    [InlineData("manual_slot", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("covered_offset", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("covered_diff", BackupPlanTaskCreationCode.InvalidSlot)]
    public void ManualIdentityAndCoveredSlotValidation(string violation, BackupPlanTaskCreationCode expected)
    {
        var plan = Plan();
        var request = Request(plan);
        request = violation switch
        {
            "wrong_plan" => request with { PlanId = Guid.NewGuid() },
            "wrong_version" => request with { PlanVersionId = Guid.NewGuid() },
            "manual_slot" => request with { ScheduledSlotAtUtc = Now.AddHours(-2) },
            "covered_offset" => request with { CoveredDifferentialSlotUtc = Now.ToOffset(TimeSpan.FromHours(8)) },
            _ => request with { Purpose = BackupRunPurpose.PlanDifferential, CoveredDifferentialSlotUtc = Now }
        };
        Assert.Equal(expected, Validate(plan, request));
    }

    [Theory]
    [InlineData("paused", BackupPlanTaskCreationCode.PlanPaused)]
    [InlineData("offset", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("future", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("missing", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("wrong_time", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("effective_boundary", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("copy_only", BackupPlanTaskCreationCode.InvalidRequest)]
    [InlineData("covered_future", BackupPlanTaskCreationCode.InvalidSlot)]
    public void ScheduledValidation(string violation, BackupPlanTaskCreationCode expected)
    {
        var plan = Plan();
        if (violation == "paused") plan.Pause();
        var request = Request(plan) with { ScheduledSlotAtUtc = Now.AddHours(-2) };
        request = violation switch
        {
            "offset" => request with { ScheduledSlotAtUtc = Now.AddHours(-2).ToOffset(TimeSpan.FromHours(8)) },
            "future" => request with { ScheduledSlotAtUtc = Now.AddDays(1).AddHours(-2) },
            "missing" => request with { ScheduledSlotAtUtc = null },
            "wrong_time" => request with { ScheduledSlotAtUtc = Now.AddMinutes(-1) },
            "effective_boundary" => request with { ScheduledSlotAtUtc = plan.CurrentVersion.EffectiveFromUtc },
            "copy_only" => request with { Purpose = BackupRunPurpose.AdHocCopyOnlyFull },
            "covered_future" => request with { CoveredDifferentialSlotUtc = Now.AddMinutes(1) },
            _ => request
        };
        Assert.Equal(expected, Validate(plan, request, BackupTaskTriggerType.Scheduled));
    }

    [Fact]
    public void VersionOwnershipUsesOpenLeftClosedRightAndManualRequiresCurrentVersion()
    {
        var plan = Plan();
        var old = Request(plan) with { ScheduledSlotAtUtc = Now.AddHours(-2) };
        plan.Revise(Guid.NewGuid(), Definition(28), old.ScheduledSlotAtUtc!.Value);
        Assert.Null(Validate(plan, old, BackupTaskTriggerType.Scheduled));
        Assert.Equal(BackupPlanTaskCreationCode.InvalidSlot,
            Validate(plan, old with { PlanVersionId = plan.CurrentVersionId }, BackupTaskTriggerType.Scheduled));
        Assert.Equal(BackupPlanTaskCreationCode.VersionConflict, Validate(plan, old with { ScheduledSlotAtUtc = null }));
        Assert.Null(Validate(plan, Request(plan)));
        Assert.Equal(BackupPlanTaskCreationCode.InvalidSlot,
            Validate(plan, old with { ScheduledSlotAtUtc = Now.AddDays(1).AddHours(-2) }, BackupTaskTriggerType.Scheduled));
    }

    [Fact]
    public void FullOnlyRejectsDiffButAllowsCopyOnly()
    {
        var plan = BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成计划", Guid.NewGuid(),
            Definition(14, BackupPlanMode.Full), Now.AddDays(-2));
        Assert.Equal(BackupPlanTaskCreationCode.PurposeNotInMode,
            Validate(plan, Request(plan) with { Purpose = BackupRunPurpose.PlanDifferential }));
        Assert.Null(Validate(plan, Request(plan) with { Purpose = BackupRunPurpose.AdHocCopyOnlyFull }));
    }

    [Fact]
    public void WeeklyScheduleRejectsWrongWeekday()
    {
        var definition = new BackupPlanDefinition(BackupPlanMode.Full,
            new(BackupScheduleType.Weekly, new(2, 0), BackupWeekdays.Thursday),
            null, null, "UTC", BackupStorageMode.LocalOnly, null, 14, null, true, false, 120, 60, 60);
        var plan = BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成计划", Guid.NewGuid(), definition, Now.AddDays(-2));
        var thursday = Request(plan) with { ScheduledSlotAtUtc = Now.AddHours(-2) };
        Assert.Null(Validate(plan, thursday, BackupTaskTriggerType.Scheduled));
        Assert.Equal(BackupPlanTaskCreationCode.InvalidSlot,
            Validate(plan, thursday with { ScheduledSlotAtUtc = Now.AddDays(-1).AddHours(-2) }, BackupTaskTriggerType.Scheduled));
    }

    [Theory]
    [InlineData(2026, 3, 8, 7, 0, 0)]
    [InlineData(2026, 11, 1, 5, 30, 0)]
    public void ScheduledSlotReusesCalculatorAtDaylightSavingBoundaries(int year, int month, int day, int hour, int minute, int second)
    {
        var slot = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        var definition = new BackupPlanDefinition(BackupPlanMode.Full,
            new(BackupScheduleType.Daily, month == 3 ? new(2, 30) : new(1, 30), BackupWeekdays.None),
            null, null, "Eastern Standard Time", BackupStorageMode.LocalOnly, null, 14, null, true, false, 120, 60, 60);
        var plan = BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成计划", Guid.NewGuid(), definition, slot.AddDays(-2));
        var request = Request(plan) with { ScheduledSlotAtUtc = slot };
        Assert.Null(BackupPlanTaskCreationRules.Validate(new(request, BackupTaskTriggerType.Scheduled, slot.AddHours(2)), plan));
        Assert.Equal(BackupPlanTaskCreationCode.InvalidSlot,
            BackupPlanTaskCreationRules.Validate(new(request with { ScheduledSlotAtUtc = slot.AddHours(1) },
                BackupTaskTriggerType.Scheduled, slot.AddHours(2)), plan));
    }

    private static BackupPlanTaskCreationCode? Validate(BackupPlan plan, CreateBackupPlanTaskRequest request,
        BackupTaskTriggerType trigger = BackupTaskTriggerType.Manual) => BackupPlanTaskCreationRules.Validate(new(request, trigger, Now), plan);
    private static BackupPlan Plan() => BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成计划", Guid.NewGuid(), Definition(14), Now.AddDays(-2));
    private static CreateBackupPlanTaskRequest Request(BackupPlan plan) => new(Guid.NewGuid(), Guid.NewGuid(), plan.Id, plan.CurrentVersionId, BackupRunPurpose.PlanFull);
    private static BackupPlanDefinition Definition(int days, BackupPlanMode mode = BackupPlanMode.FullAndDifferential) =>
        new(mode, new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None),
            mode == BackupPlanMode.Full ? null : new(BackupScheduleType.Daily, new(3, 0), BackupWeekdays.None),
            null, "UTC", BackupStorageMode.LocalOnly, null, days, null, true, false, 120, 60, 60);
    private sealed class FrozenTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class RecordingStore : IBackupPlanTaskCreationStore
    {
        internal CreateBackupPlanTaskCommand? Command { get; private set; }
        internal CancellationToken Token { get; private set; }
        public Task<BackupPlanTaskCreationResult> CreateAsync(CreateBackupPlanTaskCommand command, CancellationToken cancellationToken = default)
        {
            Command = command;
            Token = cancellationToken;
            return Task.FromResult(new BackupPlanTaskCreationResult(BackupPlanTaskCreationCode.Created));
        }
    }
}
