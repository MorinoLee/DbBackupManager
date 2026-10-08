using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupTasks;

public sealed record BackupTaskIdentitySnapshot(
    Guid ServerId,
    string ServerName,
    Guid InstanceId,
    string InstanceName,
    Guid DatabaseId,
    string DatabaseName);

public sealed record BackupSqlTargetSnapshot(
    string ConnectionAddress,
    Guid SqlCredentialReferenceId,
    bool EncryptConnection,
    bool TrustServerCertificate,
    string? CertificateTrustReason,
    int ConnectionTimeoutSeconds,
    bool AllowLegacyTls = false, string? LegacyTlsReason = null);

public sealed record BackupSourceSnapshot(
    string LocalSqlBackupRootPath,
    string FileNameRuleVersion,
    FileEndpointSettings WorkerAccess);

public sealed record BackupRemoteTargetSnapshot(
    Guid StorageTargetId,
    FileEndpointSettings Endpoint);

public sealed record BackupTaskPolicySnapshot(
    BackupStorageMode StorageMode,
    BackupRemoteTargetSnapshot? RemoteTarget,
    int? LocalRetentionDays,
    int? RemoteRetentionDays,
    bool UseChecksum,
    bool UseCompression,
    bool UseCopyOnly,
    int BackupTimeoutMinutes,
    int VerifyTimeoutMinutes,
    int TransferTimeoutMinutes,
    string TimeZoneId);

public sealed class BackupTaskSnapshot
{
    private BackupTaskSnapshot()
    {
    }

    public BackupTaskSnapshot(
        Guid taskId,
        string policyName,
        BackupTaskIdentitySnapshot identity,
        BackupSqlTargetSnapshot sqlTarget,
        BackupSourceSnapshot source,
        BackupTaskPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(sqlTarget);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);

        TaskId = BackupTaskValues.RequireId(taskId, nameof(taskId));
        PolicyName = BackupTaskValues.RequireText(policyName, 200, nameof(policyName));
        SetIdentity(identity);
        SetSqlTarget(sqlTarget);
        SetSource(source);
        SetPolicy(policy);
    }

    public Guid TaskId { get; private set; }

    // 旧策略快照保持空值，不把旧 COPY_ONLY 开关反推为计划用途。
    public BackupRunPurpose? Purpose { get; private set; }

    public static BackupTaskSnapshot ForPlan(
        BackupTask task, BackupPlanVersion version, string planName, BackupRunPurpose purpose,
        BackupTaskIdentitySnapshot identity, BackupSqlTargetSnapshot sqlTarget,
        BackupSourceSnapshot source, BackupRemoteTargetSnapshot? remoteTarget = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(version);
        if (task.PlanId is null || task.PlanVersionId is null || task.PolicyId is not null
            || task.PlanId != version.PlanId || task.PlanVersionId != version.Id
            || task.BackupType != BackupPlanRules.ToBackupType(purpose)
            || purpose != BackupRunPurpose.AdHocCopyOnlyFull && !BackupPlanRules.Includes(version.Mode, task.BackupType))
            throw new ArgumentException("计划任务身份与用途不一致。", nameof(task));
        if (purpose == BackupRunPurpose.AdHocCopyOnlyFull && task.TriggerType != BackupTaskTriggerType.Manual
            || task.CoveredDifferentialSlotUtc is not null && purpose != BackupRunPurpose.PlanFull)
            throw new ArgumentException("用途与任务触发方式或取代时隙不一致。", nameof(purpose));
        if (source.FileNameRuleVersion != "v3")
            throw new ArgumentException("计划任务快照必须显式使用 v3 路径规则。", nameof(source));
        if (remoteTarget?.StorageTargetId != version.StorageTargetId)
            throw new ArgumentException("远端配置与计划版本不一致。", nameof(remoteTarget));
        var snapshot = new BackupTaskSnapshot(task.Id, planName, identity, sqlTarget, source,
            new(version.StorageMode, remoteTarget, version.LocalRecoveryWindowDays, version.RemoteRecoveryWindowDays,
                version.UseChecksum, version.UseCompression, BackupPlanRules.UseCopyOnly(purpose),
                version.BackupTimeoutMinutes, version.VerifyTimeoutMinutes, version.TransferTimeoutMinutes, version.TimeZoneId));
        snapshot.Purpose = purpose;
        snapshot.BackupType = BackupPlanRules.ToBackupType(purpose);
        return snapshot;
    }

    public string PolicyName { get; private set; } = string.Empty;

    public Guid ServerId { get; private set; }

    public string ServerName { get; private set; } = string.Empty;

    public Guid InstanceId { get; private set; }

    public string InstanceName { get; private set; } = string.Empty;

    public Guid DatabaseId { get; private set; }

    public string DatabaseName { get; private set; } = string.Empty;

    public string ConnectionAddress { get; private set; } = string.Empty;

    public Guid SqlCredentialReferenceId { get; private set; }

    public bool EncryptConnection { get; private set; }

    public bool TrustServerCertificate { get; private set; }

    public string? CertificateTrustReason { get; private set; }

    public bool AllowLegacyTls { get; private set; }

    public string? LegacyTlsReason { get; private set; }

    public int ConnectionTimeoutSeconds { get; private set; }

    public BackupType BackupType { get; private set; }

    public bool UseChecksum { get; private set; }

    public bool UseCompression { get; private set; }

    public bool UseCopyOnly { get; private set; }

    public int BackupTimeoutMinutes { get; private set; }

    public int VerifyTimeoutMinutes { get; private set; }

    public int TransferTimeoutMinutes { get; private set; }

    public string LocalSqlBackupRootPath { get; private set; } = string.Empty;

    public string FileNameRuleVersion { get; private set; } = string.Empty;

    public FileTransferProtocol SourceAccessProtocol { get; private set; }

    public string SourceAccessHost { get; private set; } = string.Empty;

    public int? SourceAccessPort { get; private set; }

    public string SourceAccessBasePath { get; private set; } = string.Empty;

    public Guid SourceCredentialReferenceId { get; private set; }

    public string? SourceSftpHostKeyFingerprint { get; private set; }

    public BackupStorageMode StorageMode { get; private set; }

    public Guid? StorageTargetId { get; private set; }

    public FileTransferProtocol? RemoteProtocol { get; private set; }

    public string? RemoteHost { get; private set; }

    public int? RemotePort { get; private set; }

    public string? RemoteBasePath { get; private set; }

    public Guid? RemoteCredentialReferenceId { get; private set; }

    public string? RemoteSftpHostKeyFingerprint { get; private set; }

    public int? LocalRetentionDays { get; private set; }

    public int? RemoteRetentionDays { get; private set; }

    public string TimeZoneId { get; private set; } = string.Empty;

    private void SetIdentity(BackupTaskIdentitySnapshot identity)
    {
        ServerId = BackupTaskValues.RequireId(identity.ServerId, nameof(identity.ServerId));
        ServerName = BackupTaskValues.RequireText(identity.ServerName, 200, nameof(identity.ServerName));
        InstanceId = BackupTaskValues.RequireId(identity.InstanceId, nameof(identity.InstanceId));
        InstanceName = BackupTaskValues.RequireText(identity.InstanceName, 200, nameof(identity.InstanceName));
        DatabaseId = BackupTaskValues.RequireId(identity.DatabaseId, nameof(identity.DatabaseId));
        DatabaseName = BackupTaskValues.RequireText(identity.DatabaseName, 128, nameof(identity.DatabaseName));
    }

    private void SetSqlTarget(BackupSqlTargetSnapshot sqlTarget)
    {
        if (!sqlTarget.EncryptConnection)
        {
            throw new ArgumentException("任务快照中的目标 SQL 连接必须启用加密。", nameof(sqlTarget));
        }

        var trustReason = BackupTaskValues.OptionalText(
            sqlTarget.CertificateTrustReason,
            500,
            nameof(sqlTarget.CertificateTrustReason));
        if (sqlTarget.TrustServerCertificate && trustReason is null)
        {
            throw new ArgumentException("证书信任例外必须保存原因快照。", nameof(sqlTarget));
        }

        if (!sqlTarget.TrustServerCertificate && trustReason is not null)
        {
            throw new ArgumentException("未启用证书信任例外时不能保存原因。", nameof(sqlTarget));
        }

        ConnectionAddress = BackupTaskValues.RequireText(
            sqlTarget.ConnectionAddress,
            255,
            nameof(sqlTarget.ConnectionAddress));
        SqlCredentialReferenceId = BackupTaskValues.RequireId(
            sqlTarget.SqlCredentialReferenceId,
            nameof(sqlTarget.SqlCredentialReferenceId));
        LegacyTlsReason = LegacySqlCompatibility.Validate(sqlTarget.AllowLegacyTls, sqlTarget.LegacyTlsReason);
        AllowLegacyTls = sqlTarget.AllowLegacyTls;
        EncryptConnection = true;
        TrustServerCertificate = sqlTarget.TrustServerCertificate;
        CertificateTrustReason = trustReason;
        ConnectionTimeoutSeconds = BackupTaskValues.RequirePositive(
            sqlTarget.ConnectionTimeoutSeconds,
            300,
            nameof(sqlTarget.ConnectionTimeoutSeconds));
    }

    private void SetSource(BackupSourceSnapshot source)
    {
        var endpoint = source.WorkerAccess.Validate(nameof(source.WorkerAccess));
        LocalSqlBackupRootPath = BackupTaskValues.RequireText(
            source.LocalSqlBackupRootPath,
            2048,
            nameof(source.LocalSqlBackupRootPath));
        FileNameRuleVersion = BackupTaskValues.RequireText(
            source.FileNameRuleVersion,
            50,
            nameof(source.FileNameRuleVersion));
        SourceAccessProtocol = endpoint.Protocol;
        SourceAccessHost = endpoint.Host;
        SourceAccessPort = endpoint.Port;
        SourceAccessBasePath = endpoint.BasePath;
        SourceCredentialReferenceId = endpoint.CredentialReferenceId;
        SourceSftpHostKeyFingerprint = endpoint.SftpHostKeyFingerprint;
    }

    private void SetPolicy(BackupTaskPolicySnapshot policy)
    {
        BackupTaskValues.RequireDefined(policy.StorageMode, nameof(policy.StorageMode));
        var localRetention = ValidateOptionalRetention(
            policy.LocalRetentionDays,
            nameof(policy.LocalRetentionDays));
        var remoteRetention = ValidateOptionalRetention(
            policy.RemoteRetentionDays,
            nameof(policy.RemoteRetentionDays));
        var remote = ValidateRemote(policy.StorageMode, policy.RemoteTarget, localRetention, remoteRetention);

        BackupType = BackupType.Full;
        StorageMode = policy.StorageMode;
        LocalRetentionDays = localRetention;
        RemoteRetentionDays = remoteRetention;
        UseChecksum = policy.UseChecksum;
        UseCompression = policy.UseCompression;
        UseCopyOnly = policy.UseCopyOnly;
        BackupTimeoutMinutes = BackupTaskValues.RequirePositive(
            policy.BackupTimeoutMinutes,
            1440,
            nameof(policy.BackupTimeoutMinutes));
        VerifyTimeoutMinutes = BackupTaskValues.RequirePositive(
            policy.VerifyTimeoutMinutes,
            1440,
            nameof(policy.VerifyTimeoutMinutes));
        TransferTimeoutMinutes = BackupTaskValues.RequirePositive(
            policy.TransferTimeoutMinutes,
            1440,
            nameof(policy.TransferTimeoutMinutes));
        TimeZoneId = ConfigurationValues.RequireTimeZoneId(
            policy.TimeZoneId,
            nameof(policy.TimeZoneId));

        if (remote is null)
        {
            return;
        }

        StorageTargetId = remote.Value.TargetId;
        RemoteProtocol = remote.Value.Endpoint.Protocol;
        RemoteHost = remote.Value.Endpoint.Host;
        RemotePort = remote.Value.Endpoint.Port;
        RemoteBasePath = remote.Value.Endpoint.BasePath;
        RemoteCredentialReferenceId = remote.Value.Endpoint.CredentialReferenceId;
        RemoteSftpHostKeyFingerprint = remote.Value.Endpoint.SftpHostKeyFingerprint;
    }

    private static int? ValidateOptionalRetention(int? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return BackupTaskValues.RequirePositive(value.Value, 36_500, parameterName);
    }

    private static (Guid TargetId, FileEndpointSettings Endpoint)? ValidateRemote(
        BackupStorageMode storageMode,
        BackupRemoteTargetSnapshot? remoteTarget,
        int? localRetentionDays,
        int? remoteRetentionDays)
    {
        switch (storageMode)
        {
            case BackupStorageMode.LocalOnly
                when remoteTarget is null
                     && localRetentionDays is not null
                     && remoteRetentionDays is null:
                return null;

            case BackupStorageMode.LocalAndRemote
                when remoteTarget is not null
                     && localRetentionDays is not null
                     && remoteRetentionDays is not null:
                break;

            case BackupStorageMode.RemoteOnly
                when remoteTarget is not null
                     && localRetentionDays is null
                     && remoteRetentionDays is not null:
                break;

            default:
                throw new ArgumentException("任务快照的存储模式、远程目标和保留规则不一致。", nameof(remoteTarget));
        }

        var targetId = BackupTaskValues.RequireId(
            remoteTarget!.StorageTargetId,
            nameof(remoteTarget.StorageTargetId));
        var endpoint = remoteTarget.Endpoint.Validate(nameof(remoteTarget.Endpoint));
        return (targetId, endpoint);
    }
}
