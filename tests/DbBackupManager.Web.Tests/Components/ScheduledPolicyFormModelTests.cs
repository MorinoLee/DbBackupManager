using DbBackupManager.Domain.Configuration;
using DbBackupManager.Web.Components.Pages;

namespace DbBackupManager.Web.Tests.Components;

/// <summary>
/// 锁定 <see cref="ScheduledPolicyFormModel"/> 的星期位映射与 Domain <see cref="BackupWeekdays"/> 具名值一致。
/// 表单模型按 Web 分层约定不引用 Domain（见 ArchitectureTests 的 ExpectedPolicies），因此这里用测试绑定两者，
/// 而不是共享类型：<c>BackupWeekdays</c> 一旦改位序，本测试失败并迫使人工处置，而不是让表单静默产出错误星期。
/// </summary>
public sealed class ScheduledPolicyFormModelTests
{
    [Fact]
    public void EachWeekdayMapsToItsOwnBackupWeekdaysBit()
    {
        AssertDay(BackupWeekdays.Monday, model => model.Monday, (model, value) => model.Monday = value);
        AssertDay(BackupWeekdays.Tuesday, model => model.Tuesday, (model, value) => model.Tuesday = value);
        AssertDay(BackupWeekdays.Wednesday, model => model.Wednesday, (model, value) => model.Wednesday = value);
        AssertDay(BackupWeekdays.Thursday, model => model.Thursday, (model, value) => model.Thursday = value);
        AssertDay(BackupWeekdays.Friday, model => model.Friday, (model, value) => model.Friday = value);
        AssertDay(BackupWeekdays.Saturday, model => model.Saturday, (model, value) => model.Saturday = value);
        AssertDay(BackupWeekdays.Sunday, model => model.Sunday, (model, value) => model.Sunday = value);
    }

    [Fact]
    public void AllWeekdaysRoundTripThroughBackupWeekdaysCombination()
    {
        var all = BackupWeekdays.Monday
            | BackupWeekdays.Tuesday
            | BackupWeekdays.Wednesday
            | BackupWeekdays.Thursday
            | BackupWeekdays.Friday
            | BackupWeekdays.Saturday
            | BackupWeekdays.Sunday;
        var model = new ScheduledPolicyFormModel();

        model.SetDays((int)all);

        Assert.Equal((int)all, model.DaysOfWeek());
        Assert.True(model.Monday);
        Assert.True(model.Tuesday);
        Assert.True(model.Wednesday);
        Assert.True(model.Thursday);
        Assert.True(model.Friday);
        Assert.True(model.Saturday);
        Assert.True(model.Sunday);
    }

    [Fact]
    public void NoWeekdaySelectedYieldsNoneAndDoesNotSelectAnything()
    {
        var empty = new ScheduledPolicyFormModel();
        Assert.Equal((int)BackupWeekdays.None, empty.DaysOfWeek());

        empty.SetDays((int)BackupWeekdays.None);

        Assert.False(empty.Monday);
        Assert.False(empty.Tuesday);
        Assert.False(empty.Wednesday);
        Assert.False(empty.Thursday);
        Assert.False(empty.Friday);
        Assert.False(empty.Saturday);
        Assert.False(empty.Sunday);
    }

    private static void AssertDay(
        BackupWeekdays expected,
        Func<ScheduledPolicyFormModel, bool> read,
        Action<ScheduledPolicyFormModel, bool> write)
    {
        var model = new ScheduledPolicyFormModel();
        write(model, true);

        Assert.Equal((int)expected, model.DaysOfWeek());

        var restored = new ScheduledPolicyFormModel();
        restored.SetDays((int)expected);

        Assert.True(read(restored));
        Assert.Equal((int)expected, restored.DaysOfWeek());
    }
}
