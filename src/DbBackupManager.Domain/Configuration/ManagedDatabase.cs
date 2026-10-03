using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Configuration;

public sealed class ManagedDatabase : ConcurrentEntity
{
    private ManagedDatabase()
    {
    }

    public ManagedDatabase(
        Guid id,
        Guid instanceId,
        string databaseName,
        bool isSystemDatabase,
        bool isAvailable,
        DateTimeOffset discoveredAtUtc,
        string? recoveryModel = null,
        string? stateDescription = null)
        : base(id)
    {
        InstanceId = ConfigurationValues.RequireId(instanceId, nameof(instanceId));
        SetName(databaseName);
        IsSystemDatabase = isSystemDatabase;
        IsAvailable = isAvailable;
        LastDiscoveredAtUtc = ConfigurationValues.RequireUtc(discoveredAtUtc, nameof(discoveredAtUtc));
        RecoveryModel = ConfigurationValues.OptionalText(recoveryModel, 60, nameof(recoveryModel));
        StateDescription = ConfigurationValues.OptionalText(
            stateDescription,
            60,
            nameof(stateDescription));
    }

    public Guid InstanceId { get; private set; }

    public string DatabaseName { get; private set; } = string.Empty;

    public string NormalizedDatabaseName { get; private set; } = string.Empty;

    public string? RecoveryModel { get; private set; }

    public string? StateDescription { get; private set; }

    public bool IsSystemDatabase { get; private set; }

    public bool IsManaged { get; private set; }

    public bool IsAvailable { get; private set; }

    public DateTimeOffset LastDiscoveredAtUtc { get; private set; }

    public void RefreshDiscovery(
        DateTimeOffset discoveredAtUtc,
        bool isSystemDatabase,
        bool isAvailable,
        string? recoveryModel,
        string? stateDescription)
    {
        var validatedAtUtc = ConfigurationValues.RequireUtc(discoveredAtUtc, nameof(discoveredAtUtc));
        var validatedRecoveryModel = ConfigurationValues.OptionalText(
            recoveryModel,
            60,
            nameof(recoveryModel));
        var validatedState = ConfigurationValues.OptionalText(
            stateDescription,
            60,
            nameof(stateDescription));

        IsSystemDatabase = isSystemDatabase;
        IsAvailable = isAvailable;
        LastDiscoveredAtUtc = validatedAtUtc;
        RecoveryModel = validatedRecoveryModel;
        StateDescription = validatedState;

        if (IsSystemDatabase)
        {
            IsManaged = false;
        }
    }

    public void SetManaged(bool isManaged)
    {
        if (isManaged && IsSystemDatabase)
        {
            throw new InvalidOperationException("系统数据库不能设为纳管备份数据库。");
        }

        IsManaged = isManaged;
    }

    private void SetName(string databaseName)
    {
        DatabaseName = ConfigurationValues.RequireText(databaseName, 128, nameof(databaseName));
        NormalizedDatabaseName = ConfigurationValues.Normalize(DatabaseName);
    }
}
