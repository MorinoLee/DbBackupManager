using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.BackupPlans;

public sealed class BackupPlanVersion : ConcurrentEntity
{
    private BackupPlanVersion()
    {
    }

    internal BackupPlanVersion(
        Guid id,
        Guid planId,
        int number,
        BackupPlanDefinition definition,
        DateTimeOffset effectiveFromUtc)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (number < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(number), "计划版本号必须从 1 开始。");
        }

        PlanId = ConfigurationValues.RequireId(planId, nameof(planId));
        Number = number;
        Mode = definition.Mode;
        FullSchedule = definition.FullSchedule;
        DifferentialSchedule = definition.DifferentialSchedule;
        LogSchedule = definition.LogSchedule;
        TimeZoneId = definition.TimeZoneId;
        StorageMode = definition.StorageMode;
        StorageTargetId = definition.StorageTargetId;
        RecoveryWindowDays = definition.RecoveryWindowDays;
        UseChecksum = definition.UseChecksum;
        UseCompression = definition.UseCompression;
        BackupTimeoutMinutes = definition.BackupTimeoutMinutes;
        VerifyTimeoutMinutes = definition.VerifyTimeoutMinutes;
        TransferTimeoutMinutes = definition.TransferTimeoutMinutes;
        EffectiveFromUtc = ConfigurationValues.RequireUtc(effectiveFromUtc, nameof(effectiveFromUtc));
    }

    public Guid PlanId { get; private set; }

    public int Number { get; private set; }

    public BackupPlanMode Mode { get; private set; }

    public RecurringBackupSchedule FullSchedule { get; private set; }

    public RecurringBackupSchedule? DifferentialSchedule { get; private set; }

    public LogBackupSchedule? LogSchedule { get; private set; }

    public string TimeZoneId { get; private set; } = string.Empty;

    public BackupStorageMode StorageMode { get; private set; }

    public Guid? StorageTargetId { get; private set; }

    public int RecoveryWindowDays { get; private set; }

    public bool UseChecksum { get; private set; }

    public bool UseCompression { get; private set; }

    public int BackupTimeoutMinutes { get; private set; }

    public int VerifyTimeoutMinutes { get; private set; }

    public int TransferTimeoutMinutes { get; private set; }

    public DateTimeOffset EffectiveFromUtc { get; private set; }

    internal bool HasSameConfiguration(BackupPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Mode == definition.Mode
            && FullSchedule.Equals(definition.FullSchedule)
            && Nullable.Equals(DifferentialSchedule, definition.DifferentialSchedule)
            && Nullable.Equals(LogSchedule, definition.LogSchedule)
            && string.Equals(TimeZoneId, definition.TimeZoneId, StringComparison.Ordinal)
            && StorageMode == definition.StorageMode
            && StorageTargetId == definition.StorageTargetId
            && RecoveryWindowDays == definition.RecoveryWindowDays
            && UseChecksum == definition.UseChecksum
            && UseCompression == definition.UseCompression
            && BackupTimeoutMinutes == definition.BackupTimeoutMinutes
            && VerifyTimeoutMinutes == definition.VerifyTimeoutMinutes
            && TransferTimeoutMinutes == definition.TransferTimeoutMinutes;
    }
}

public sealed class BackupPlan : ConcurrentEntity
{
    private readonly List<BackupPlanVersion> _versions = [];
    private readonly IReadOnlyList<BackupPlanVersion> _versionView;

    private BackupPlan()
    {
        _versionView = _versions.AsReadOnly();
    }

    private BackupPlan(Guid id, Guid databaseId, string name)
        : base(id)
    {
        _versionView = _versions.AsReadOnly();
        DatabaseId = ConfigurationValues.RequireId(databaseId, nameof(databaseId));
        Rename(name);
    }

    public Guid DatabaseId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public bool IsPaused { get; private set; }

    public Guid CurrentVersionId { get; private set; }

    public BackupPlanVersion CurrentVersion => _versions.Single(version => version.Id == CurrentVersionId);

    public IReadOnlyList<BackupPlanVersion> Versions => _versionView;

    public static BackupPlan Create(
        Guid id,
        Guid databaseId,
        string name,
        Guid initialVersionId,
        BackupPlanDefinition definition,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var plan = new BackupPlan(id, databaseId, name);
        var version = new BackupPlanVersion(
            initialVersionId,
            id,
            number: 1,
            definition,
            ConfigurationValues.RequireUtc(nowUtc, nameof(nowUtc)));
        plan._versions.Add(version);
        plan.CurrentVersionId = version.Id;
        return plan;
    }

    public void Rename(string name)
    {
        var validatedName = ConfigurationValues.RequireText(name, 200, nameof(name));
        Name = validatedName;
        NormalizedName = ConfigurationValues.Normalize(validatedName);
    }

    public void Pause()
    {
        IsPaused = true;
    }

    public void Resume()
    {
        IsPaused = false;
    }

    public bool Revise(Guid versionId, BackupPlanDefinition definition, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (CurrentVersion.HasSameConfiguration(definition))
        {
            return false;
        }

        var effectiveFromUtc = ConfigurationValues.RequireUtc(nowUtc, nameof(nowUtc));
        if (effectiveFromUtc <= CurrentVersion.EffectiveFromUtc)
        {
            throw new ArgumentException("新版本的生效时间必须晚于当前版本。", nameof(nowUtc));
        }

        ConfigurationValues.RequireId(versionId, nameof(versionId));
        if (_versions.Any(version => version.Id == versionId))
        {
            throw new ArgumentException("计划版本标识已存在。", nameof(versionId));
        }

        var version = new BackupPlanVersion(versionId, Id, _versions.Count + 1, definition, effectiveFromUtc);
        _versions.Add(version);
        CurrentVersionId = version.Id;
        return true;
    }

    public DateTimeOffset? SupersededAtUtc(Guid versionId)
    {
        var ordered = _versions.OrderBy(version => version.Number).ToArray();
        var index = Array.FindIndex(ordered, version => version.Id == versionId);
        if (index < 0)
        {
            throw new ArgumentException("计划版本不存在。", nameof(versionId));
        }

        return index == ordered.Length - 1 ? null : ordered[index + 1].EffectiveFromUtc;
    }
}

public sealed class CurrentBackupPlanIndex
{
    private readonly Dictionary<Guid, Guid> _planIdByDatabase = [];

    public void Add(BackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_planIdByDatabase.TryAdd(plan.DatabaseId, plan.Id))
        {
            throw new InvalidOperationException("每个受管数据库只能有一个当前备份计划。");
        }
    }

    public bool Occupies(Guid databaseId)
    {
        return _planIdByDatabase.ContainsKey(databaseId);
    }
}
