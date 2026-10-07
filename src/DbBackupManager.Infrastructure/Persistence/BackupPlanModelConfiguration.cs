using System.Reflection;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DbBackupManager.Infrastructure.Persistence;

internal static class BackupPlanMigrationGuard
{
    internal const string RejectDownWhenPlansExist = """
        IF EXISTS (SELECT 1 FROM [BackupPlans])
        BEGIN
            THROW 51000, N'已写入备份计划，不能回退该迁移。请从平台库备份恢复。', 1;
        END
        """;
}

internal sealed class BackupPlanStore(IDbContextFactory<PlatformDbContext> factory)
{
    public async Task AddAsync(BackupPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var versionId = plan.CurrentVersionId;
        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            // 暂时清空当前版本指针，先插入计划和版本，再在同一事务内补齐循环外键。
            CurrentVersionField.SetValue(plan, null);
            context.BackupPlans.Add(plan);
            await context.SaveChangesAsync(cancellationToken);
            context.Entry(plan).Property<Guid?>("_currentVersionId").CurrentValue = versionId;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static readonly FieldInfo CurrentVersionField = typeof(BackupPlan).GetField(
        "_currentVersionId",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
}

internal sealed class BackupPlanConfiguration : IEntityTypeConfiguration<BackupPlan>
{
    public void Configure(EntityTypeBuilder<BackupPlan> builder)
    {
        builder.ToTable("BackupPlans", table =>
        {
            table.HasCheckConstraint("CK_BackupPlans_Name_NotEmpty", "LEN([Name]) > 0");
            table.HasCheckConstraint(
                "CK_BackupPlans_NormalizedName_NotEmpty",
                "LEN([NormalizedName]) > 0");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsPaused).IsRequired();
        builder.Ignore(x => x.CurrentVersionId);
        builder.Property<Guid?>("_currentVersionId").HasColumnName("CurrentVersionId");
        builder.HasIndex(x => x.DatabaseId)
            .IsUnique()
            .HasDatabaseName("UX_BackupPlans_DatabaseId");
        builder.HasOne<ManagedDatabase>()
            .WithMany()
            .HasForeignKey(x => x.DatabaseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Versions)
            .WithOne()
            .HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasOne<BackupPlanVersion>()
            .WithMany()
            .HasForeignKey("Id", "_currentVersionId")
            .HasPrincipalKey(
                nameof(BackupPlanVersion.PlanId),
                nameof(BackupPlanVersion.Id))
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupPlanVersionConfiguration : IEntityTypeConfiguration<BackupPlanVersion>
{
    public void Configure(EntityTypeBuilder<BackupPlanVersion> builder)
    {
        builder.ToTable("BackupPlanVersions", table =>
        {
            table.HasCheckConstraint("CK_BackupPlanVersions_Number", "[Number] >= 1");
            table.HasCheckConstraint("CK_BackupPlanVersions_TimeZoneId_NotEmpty", "LEN([TimeZoneId]) > 0");
            table.HasCheckConstraint(
                "CK_BackupPlanVersions_ModeSchedules",
                "([Mode] = 'Full' AND [DifferentialSchedule_ScheduleType] IS NULL "
                + "AND [LogSchedule_IntervalMinutes] IS NULL AND [LogSchedule_AnchorUtc] IS NULL) OR "
                + "([Mode] = 'FullAndDifferential' AND [DifferentialSchedule_ScheduleType] IS NOT NULL "
                + "AND [LogSchedule_IntervalMinutes] IS NULL AND [LogSchedule_AnchorUtc] IS NULL) OR "
                + "([Mode] = 'FullAndDifferentialAndLog' AND [DifferentialSchedule_ScheduleType] IS NOT NULL "
                + "AND [LogSchedule_IntervalMinutes] IS NOT NULL AND [LogSchedule_AnchorUtc] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_BackupPlanVersions_Schedule",
                ScheduleClause("FullSchedule")
                + " AND (([DifferentialSchedule_ScheduleType] IS NULL "
                + "AND [DifferentialSchedule_LocalTime] IS NULL "
                + "AND [DifferentialSchedule_DaysOfWeek] IS NULL) OR "
                + ScheduleClause("DifferentialSchedule") + ")");
            table.HasCheckConstraint(
                "CK_BackupPlanVersions_Timeouts",
                "[BackupTimeoutMinutes] BETWEEN 1 AND 1440 "
                + "AND [VerifyTimeoutMinutes] BETWEEN 1 AND 1440 "
                + "AND [TransferTimeoutMinutes] BETWEEN 1 AND 1440");
            table.HasCheckConstraint(
                "CK_BackupPlanVersions_LogInterval",
                "([LogSchedule_IntervalMinutes] IS NULL AND [LogSchedule_AnchorUtc] IS NULL) OR "
                + "([LogSchedule_IntervalMinutes] IS NOT NULL "
                + "AND [LogSchedule_IntervalMinutes] BETWEEN 1 AND 1440 AND [LogSchedule_AnchorUtc] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_BackupPlanVersions_UtcOffset",
                "DATEPART(TZOFFSET, [EffectiveFromUtc]) = 0 AND "
                + "([LogSchedule_AnchorUtc] IS NULL OR DATEPART(TZOFFSET, [LogSchedule_AnchorUtc]) = 0)");
            table.HasCheckConstraint(
                "CK_BackupPlanVersions_Windows",
                "([StorageMode] = 'LocalOnly' AND [StorageTargetId] IS NULL "
                + "AND [LocalRecoveryWindowDays] IS NOT NULL "
                + "AND [LocalRecoveryWindowDays] BETWEEN 1 AND 36500 AND [RemoteRecoveryWindowDays] IS NULL) OR "
                + "([StorageMode] = 'LocalAndRemote' AND [StorageTargetId] IS NOT NULL "
                + "AND [LocalRecoveryWindowDays] IS NOT NULL AND [LocalRecoveryWindowDays] BETWEEN 1 AND 36500 "
                + "AND [RemoteRecoveryWindowDays] IS NOT NULL AND [RemoteRecoveryWindowDays] BETWEEN 1 AND 36500) OR "
                + "([StorageMode] = 'RemoteOnly' AND [StorageTargetId] IS NOT NULL "
                + "AND [LocalRecoveryWindowDays] IS NULL AND [RemoteRecoveryWindowDays] IS NOT NULL "
                + "AND [RemoteRecoveryWindowDays] BETWEEN 1 AND 36500)");
        });
        builder.ConfigureConcurrency();
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasAlternateKey(x => new { x.PlanId, x.Id })
            .HasName("AK_BackupPlanVersions_PlanId_Id");
        builder.Property(x => x.Number).IsRequired();
        builder.Property(x => x.Mode).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.TimeZoneId).HasMaxLength(150).IsRequired();
        builder.Property(x => x.StorageMode).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.EffectiveFromUtc).HasPrecision(7).IsRequired();
        builder.Property(x => x.UseChecksum).IsRequired();
        builder.Property(x => x.UseCompression).IsRequired();
        ConfigureSchedule(builder.ComplexProperty(x => x.FullSchedule), "FullSchedule");
        var differential = builder.ComplexProperty(x => x.DifferentialSchedule);
        differential.IsRequired(false);
        ConfigureSchedule(differential, "DifferentialSchedule");
        var log = builder.ComplexProperty(x => x.LogSchedule);
        log.IsRequired(false);
        log.Property(x => x.IntervalMinutes).HasColumnName("LogSchedule_IntervalMinutes");
        log.Property(x => x.AnchorUtc).HasPrecision(7).HasColumnName("LogSchedule_AnchorUtc");
        builder.HasIndex(x => new { x.PlanId, x.Number })
            .IsUnique()
            .HasDatabaseName("UX_BackupPlanVersions_PlanId_Number");
        builder.HasOne<StorageTarget>()
            .WithMany()
            .HasForeignKey(x => x.StorageTargetId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSchedule<TSchedule>(
        ComplexPropertyBuilder<TSchedule> schedule,
        string prefix)
        where TSchedule : struct
    {
        schedule.Property(nameof(RecurringBackupSchedule.ScheduleType))
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasColumnName($"{prefix}_ScheduleType");
        schedule.Property(nameof(RecurringBackupSchedule.LocalTime))
            .HasColumnType("time(0)")
            .HasColumnName($"{prefix}_LocalTime");
        schedule.Property(nameof(RecurringBackupSchedule.DaysOfWeek))
            .HasConversion<int>()
            .HasColumnName($"{prefix}_DaysOfWeek");
    }

    private static string ScheduleClause(string prefix) =>
        $"([{prefix}_ScheduleType] IS NOT NULL AND [{prefix}_LocalTime] IS NOT NULL "
        + $"AND [{prefix}_DaysOfWeek] IS NOT NULL AND [{prefix}_ScheduleType] IN ('Daily', 'Weekly') AND "
        + $"(([{prefix}_ScheduleType] = 'Daily' AND [{prefix}_DaysOfWeek] = 0) OR "
        + $"([{prefix}_ScheduleType] = 'Weekly' AND [{prefix}_DaysOfWeek] BETWEEN 1 AND 127)))";
}
