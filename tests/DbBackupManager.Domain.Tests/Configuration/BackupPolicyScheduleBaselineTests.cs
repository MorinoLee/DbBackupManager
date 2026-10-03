using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.Configuration;

public sealed class BackupPolicyScheduleBaselineTests
{
    [Fact]
    public void CreatingEnabledScheduledPolicyUsesProvidedNowUtc()
    {
        var now = new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero);
        var policy = CreatePolicy(isEnabled: true, isManualOnly: false, now);

        Assert.Equal(now, policy.ScheduleEffectiveFromUtc);
        Assert.True(policy.IsEnabled);
        Assert.False(policy.IsManualOnly);
    }

    [Fact]
    public void CreatingEnabledScheduledPolicyWithoutNowUtcFailsClosed()
    {
        Assert.Throws<ArgumentException>(() => CreatePolicy(isEnabled: true, isManualOnly: false));
    }

    [Fact]
    public void CreatingDisabledScheduledPolicyLeavesBaselineEmpty()
    {
        var policy = CreatePolicy(isEnabled: false, isManualOnly: false);

        Assert.Null(policy.ScheduleEffectiveFromUtc);
        Assert.False(policy.IsEnabled);
    }

    [Fact]
    public void ManualPolicyNeverHasScheduleBaseline()
    {
        var now = new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero);
        var policy = CreatePolicy(isEnabled: true, isManualOnly: true, now);

        Assert.Null(policy.ScheduleEffectiveFromUtc);
        policy.SetEnabled(false);
        policy.SetEnabled(true, now.AddHours(1));
        policy.Update("手动改名", policy.ToSettings() with { LocalTime = new TimeOnly(4, 0) }, now.AddHours(2));
        Assert.Null(policy.ScheduleEffectiveFromUtc);
        Assert.True(policy.IsManualOnly);
    }

    [Fact]
    public void EnablingScheduledPolicyResetsBaseline()
    {
        var created = new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero);
        var enabledAt = created.AddHours(3);
        var policy = CreatePolicy(isEnabled: false, isManualOnly: false);

        policy.SetEnabled(true, enabledAt);

        Assert.Equal(enabledAt, policy.ScheduleEffectiveFromUtc);
        policy.SetEnabled(true, enabledAt.AddHours(1));
        Assert.Equal(enabledAt, policy.ScheduleEffectiveFromUtc);
    }

    [Fact]
    public void DisablingScheduledPolicyClearsBaseline()
    {
        var policy = CreatePolicy(
            isEnabled: true,
            isManualOnly: false,
            new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero));

        policy.SetEnabled(false);

        Assert.False(policy.IsEnabled);
        Assert.Null(policy.ScheduleEffectiveFromUtc);
    }

    [Fact]
    public void ScheduleFieldChangeResetsEnabledBaseline()
    {
        var created = new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero);
        var changed = created.AddHours(2);
        var policy = CreatePolicy(isEnabled: true, isManualOnly: false, created);

        policy.Update("计划策略", policy.ToSettings() with { LocalTime = new TimeOnly(5, 30) }, changed);

        Assert.Equal(changed, policy.ScheduleEffectiveFromUtc);
        Assert.Equal(new TimeOnly(5, 30), policy.LocalTime);
    }

    [Fact]
    public void NonScheduleFieldChangeDoesNotResetBaseline()
    {
        var created = new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero);
        var policy = CreatePolicy(isEnabled: true, isManualOnly: false, created);

        policy.Update("新名称", policy.ToSettings() with
        {
            LocalRetentionDays = 14,
            BackupTimeoutMinutes = 90,
            UseCompression = true
        }, created.AddHours(4));

        Assert.Equal(created, policy.ScheduleEffectiveFromUtc);
        Assert.Equal("新名称", policy.Name);
        Assert.Equal(14, policy.LocalRetentionDays);
    }

    [Fact]
    public void EnablingScheduledPolicyWithoutNowUtcFailsClosed()
    {
        var policy = CreatePolicy(isEnabled: false, isManualOnly: false);
        Assert.Throws<ArgumentException>(() => policy.SetEnabled(true));
        Assert.False(policy.IsEnabled);
        Assert.Null(policy.ScheduleEffectiveFromUtc);
    }

    [Fact]
    public void ChangingScheduleWithoutNowUtcFailsClosed()
    {
        var created = new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero);
        var policy = CreatePolicy(isEnabled: true, isManualOnly: false, created);

        Assert.Throws<ArgumentException>(() =>
            policy.Update("计划策略", policy.ToSettings() with { TimeZoneId = "UTC" }));
        Assert.Equal(created, policy.ScheduleEffectiveFromUtc);
        Assert.Equal("Taipei Standard Time", policy.TimeZoneId);
    }

    private static BackupPolicy CreatePolicy(bool isEnabled, bool isManualOnly, DateTimeOffset? nowUtc = null)
    {
        return new BackupPolicy(
            Guid.NewGuid(),
            "计划策略",
            Guid.NewGuid(),
            new BackupPolicySettings(
                BackupStorageMode.LocalOnly,
                null,
                BackupScheduleType.Daily,
                new TimeOnly(2, 0),
                BackupWeekdays.None,
                "Taipei Standard Time",
                7,
                null,
                true,
                false,
                true,
                120,
                30,
                60),
            isEnabled,
            isManualOnly,
            nowUtc);
    }
}
