using System.ComponentModel.DataAnnotations;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Web.Components.Pages;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupPlanFormModelTests
{
    [Fact]
    public void ChecksumDefaultsToCheckedAndLogModeIsRejected()
    {
        var model = new BackupPlanFormModel();
        Assert.True(model.UseChecksum);
        Assert.Equal(1, model.Mode);

        model.Name = "合成计划";
        model.DatabaseId = Guid.NewGuid();
        model.Mode = 3;
        var errors = model.Validate(new ValidationContext(model)).ToArray();
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(BackupPlanFormModel.Mode)));
    }

    [Fact]
    public void LocalFullPlanOmitsRemoteWindowAndDifferentialSchedule()
    {
        var model = new BackupPlanFormModel
        {
            Name = " 合成计划 ",
            DatabaseId = Guid.NewGuid(),
            Mode = 1,
            StorageMode = 1,
            FullMonday = true,
            UseChecksum = true,
        };

        var input = model.ToInput();

        Assert.Equal("合成计划", input.Name);
        Assert.True(input.UseChecksum);
        Assert.Null(input.DifferentialSchedule);
        Assert.Null(input.StorageTargetId);
        Assert.Equal(7, input.LocalRecoveryWindowDays);
        Assert.Null(input.RemoteRecoveryWindowDays);
        Assert.Equal(0, input.FullSchedule.DaysOfWeek);
    }

    [Fact]
    public void RemoteDifferentialPlanKeepsTargetAndClearsLocalWindow()
    {
        var target = Guid.NewGuid();
        var view = new BackupPlanView(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "远端计划",
            true,
            "version",
            Guid.NewGuid(),
            4,
            BackupPlanMode.FullAndDifferential,
            new RecurringBackupSchedule(BackupScheduleType.Weekly, new TimeOnly(2, 30), BackupWeekdays.Monday | BackupWeekdays.Friday),
            new RecurringBackupSchedule(BackupScheduleType.Daily, new TimeOnly(3, 15), BackupWeekdays.None),
            "UTC",
            BackupStorageMode.RemoteOnly,
            target,
            null,
            30,
            false,
            true,
            90,
            45,
            20,
            DateTimeOffset.UtcNow,
            []);

        var model = BackupPlanFormModel.FromView(view);
        Assert.True(model.FullMonday);
        Assert.True(model.FullFriday);
        Assert.False(model.FullSunday);
        Assert.False(model.UseChecksum);
        Assert.True(model.IsPaused);
        Assert.Equal(7, model.LocalRecoveryWindowDays);

        var input = model.ToInput();
        Assert.Equal(2, input.Mode);
        Assert.Equal(target, input.StorageTargetId);
        Assert.Null(input.LocalRecoveryWindowDays);
        Assert.Equal(30, input.RemoteRecoveryWindowDays);
        Assert.False(input.UseChecksum);
        Assert.Equal((int)(BackupWeekdays.Monday | BackupWeekdays.Friday), input.FullSchedule.DaysOfWeek);
        Assert.NotNull(input.DifferentialSchedule);
        Assert.Equal(0, input.DifferentialSchedule!.DaysOfWeek);
        Assert.Equal(new TimeOnly(3, 15), input.DifferentialSchedule.LocalTime);
    }
}
