using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.TargetSql;

public enum TargetSqlOutcome
{
    Succeeded,
    ConfirmedFailed,
    Indeterminate,
}

public enum TargetSqlFailurePhase
{
    RequestValidation,
    CredentialResolution,
    ConnectionOpen,
    ServerProbe,
    DatabaseDiscovery,
    BackupExecution,
    BackupIdentityInspection,
    BackupVerification,
    ResponseProcessing,
}

public enum TargetSqlFailureCode
{
    InvalidRequest,
    CredentialUnavailable,
    CredentialInvalid,
    AuthenticationFailed,
    AuthorizationDenied,
    TransportSecurityFailed,
    ConnectionFailed,
    UnsupportedServerVersion,
    DatabaseNotFound,
    DatabaseUnavailable,
    CompressionUnsupported,
    BackupDestinationUnavailable,
    CommandRejected,
    TimedOut,
    Cancelled,
    ConnectionInterrupted,
    InvalidResponse,
    BackupTypeNotValidated,
    DifferentialBaseMissing,
}

public enum TargetSqlDatabaseState
{
    Online,
    Restoring,
    Recovering,
    RecoveryPending,
    Suspect,
    Emergency,
    Offline,
    Copying,
    OfflineSecondary,
    Unknown,
}

public enum TargetSqlRecoveryModel
{
    Full,
    BulkLogged,
    Simple,
    Unknown,
}

public enum TargetSqlBackupIdentityStatus
{
    NotFound,
    InProgress,
    CompletedMatching,
    IdentityMismatch,
}

public sealed record TargetSqlFailure(
    TargetSqlFailureCode Code,
    TargetSqlFailurePhase Phase);

public sealed class TargetSqlResult<T>
    where T : class
{
    internal TargetSqlResult(
        TargetSqlOutcome outcome,
        T? value,
        TargetSqlFailure? failure)
    {
        if (outcome == TargetSqlOutcome.Succeeded)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (failure is not null)
            {
                throw new ArgumentException("成功结果不能包含失败分类。", nameof(failure));
            }
        }
        else
        {
            ArgumentNullException.ThrowIfNull(failure);
            if (value is not null)
            {
                throw new ArgumentException("失败或不确定结果不能包含成功值。", nameof(value));
            }
        }

        Outcome = outcome;
        Value = value;
        Failure = failure;
    }

    public TargetSqlOutcome Outcome { get; }

    public T? Value { get; }

    public TargetSqlFailure? Failure { get; }

    public bool IsSucceeded => Outcome == TargetSqlOutcome.Succeeded;

}

public static class TargetSqlResult
{
    public static TargetSqlResult<T> Succeeded<T>(T value)
        where T : class
    {
        return new TargetSqlResult<T>(TargetSqlOutcome.Succeeded, value, null);
    }

    public static TargetSqlResult<T> ConfirmedFailure<T>(
        TargetSqlFailureCode code,
        TargetSqlFailurePhase phase)
        where T : class
    {
        return new TargetSqlResult<T>(
            TargetSqlOutcome.ConfirmedFailed,
            null,
            new TargetSqlFailure(code, phase));
    }

    public static TargetSqlResult<T> Indeterminate<T>(
        TargetSqlFailureCode code,
        TargetSqlFailurePhase phase)
        where T : class
    {
        return new TargetSqlResult<T>(
            TargetSqlOutcome.Indeterminate,
            null,
            new TargetSqlFailure(code, phase));
    }
}

public sealed class TargetSqlConnectionInput
{
    public TargetSqlConnectionInput(
        string connectionAddress,
        Guid credentialReferenceId,
        bool encryptConnection,
        bool trustServerCertificate,
        string? certificateTrustReason,
        int connectionTimeoutSeconds,
        bool allowLegacyTls = false, string? legacyTlsReason = null)
    {
        if (!encryptConnection)
        {
            throw new ArgumentException("目标 SQL 连接必须启用传输加密。", nameof(encryptConnection));
        }

        var trustReason = TargetSqlContractValues.OptionalSafeText(
            certificateTrustReason,
            500,
            nameof(certificateTrustReason));
        if (trustServerCertificate && trustReason is null)
        {
            throw new ArgumentException(
                "显式信任服务器证书时必须提供受控例外原因。",
                nameof(certificateTrustReason));
        }

        if (!trustServerCertificate && trustReason is not null)
        {
            throw new ArgumentException(
                "未启用证书信任例外时不能提供例外原因。",
                nameof(certificateTrustReason));
        }

        ConnectionAddress = TargetSqlContractValues.ConnectionAddress(
            connectionAddress,
            nameof(connectionAddress));
        CredentialReferenceId = TargetSqlContractValues.RequiredId(
            credentialReferenceId,
            nameof(credentialReferenceId));
        LegacyTlsReason = DbBackupManager.Domain.Configuration.LegacySqlCompatibility.Validate(allowLegacyTls, legacyTlsReason);
        AllowLegacyTls = allowLegacyTls;
        EncryptConnection = true;
        TrustServerCertificate = trustServerCertificate;
        CertificateTrustReason = trustReason;
        ConnectionTimeoutSeconds = TargetSqlContractValues.Positive(
            connectionTimeoutSeconds,
            300,
            nameof(connectionTimeoutSeconds));
    }

    public string ConnectionAddress { get; }

    public Guid CredentialReferenceId { get; }

    public bool EncryptConnection { get; }

    public bool TrustServerCertificate { get; }

    public string? CertificateTrustReason { get; }

    public bool AllowLegacyTls { get; }

    public string? LegacyTlsReason { get; }

    public int ConnectionTimeoutSeconds { get; }
}

public sealed class TargetSqlFullBackupRequest
{
    public TargetSqlFullBackupRequest(
        string databaseName,
        string localSqlFilePath,
        bool useCopyOnly,
        bool useChecksum,
        bool useCompression,
        int commandTimeoutSeconds)
    {
        DatabaseName = TargetSqlContractValues.DatabaseName(
            databaseName,
            nameof(databaseName));
        LocalSqlFilePath = TargetSqlContractValues.BackupFilePath(
            localSqlFilePath,
            nameof(localSqlFilePath));
        UseCopyOnly = useCopyOnly;
        UseChecksum = useChecksum;
        UseCompression = useCompression;
        CommandTimeoutSeconds = TargetSqlContractValues.Positive(
            commandTimeoutSeconds,
            86_400,
            nameof(commandTimeoutSeconds));
    }

    public string DatabaseName { get; }

    public string LocalSqlFilePath { get; }

    public bool UseCopyOnly { get; }

    public bool UseChecksum { get; }

    public bool UseCompression { get; }

    public int CommandTimeoutSeconds { get; }
}

public sealed class TargetSqlBackupRequest
{
    public TargetSqlBackupRequest(
        string databaseName,
        string localSqlFilePath,
        BackupRunPurpose purpose,
        bool useChecksum,
        bool useCompression,
        int commandTimeoutSeconds)
    {
        BackupType = BackupPlanRules.ToBackupType(purpose);
        if (BackupType == BackupType.Log)
        {
            throw new ArgumentException("当前执行端口尚不支持日志备份。", nameof(purpose));
        }

        Purpose = purpose;
        UseCopyOnly = BackupPlanRules.UseCopyOnly(purpose);
        DatabaseName = TargetSqlContractValues.DatabaseName(databaseName, nameof(databaseName));
        LocalSqlFilePath = TargetSqlContractValues.BackupFilePath(localSqlFilePath, nameof(localSqlFilePath));
        UseChecksum = useChecksum;
        UseCompression = useCompression;
        CommandTimeoutSeconds = TargetSqlContractValues.Positive(commandTimeoutSeconds, 86_400, nameof(commandTimeoutSeconds));
    }

    public string DatabaseName { get; }
    public string LocalSqlFilePath { get; }
    public BackupRunPurpose Purpose { get; }
    public BackupType BackupType { get; }
    public bool UseCopyOnly { get; }
    public bool UseChecksum { get; }
    public bool UseCompression { get; }
    public int CommandTimeoutSeconds { get; }
}

public sealed record TargetSqlBackupCompletion(
    BackupType BackupType,
    bool UsedCopyOnly,
    bool UsedChecksum,
    bool UsedCompression);

public sealed class TargetSqlBackupVerificationRequest
{
    public TargetSqlBackupVerificationRequest(
        string localSqlFilePath,
        bool useChecksum,
        int commandTimeoutSeconds)
    {
        LocalSqlFilePath = TargetSqlContractValues.BackupFilePath(
            localSqlFilePath,
            nameof(localSqlFilePath));
        UseChecksum = useChecksum;
        CommandTimeoutSeconds = TargetSqlContractValues.Positive(
            commandTimeoutSeconds,
            86_400,
            nameof(commandTimeoutSeconds));
    }

    public string LocalSqlFilePath { get; }

    public bool UseChecksum { get; }

    public int CommandTimeoutSeconds { get; }
}

public sealed class TargetSqlBackupIdentityRequest
{
    public TargetSqlBackupIdentityRequest(
        string databaseName,
        string localSqlFilePath,
        int commandTimeoutSeconds)
    {
        DatabaseName = TargetSqlContractValues.DatabaseName(databaseName, nameof(databaseName));
        LocalSqlFilePath = TargetSqlContractValues.BackupFilePath(localSqlFilePath, nameof(localSqlFilePath));
        CommandTimeoutSeconds = TargetSqlContractValues.Positive(
            commandTimeoutSeconds,
            86_400,
            nameof(commandTimeoutSeconds));
    }

    public string DatabaseName { get; }

    public string LocalSqlFilePath { get; }

    public int CommandTimeoutSeconds { get; }
}

public sealed record TargetSqlServerInfo(
    string ProductVersion,
    string? ProductLevel,
    string? Edition,
    int EngineEdition,
    int MajorVersion,
    bool SupportsBackupCompression);

public sealed record TargetSqlDatabaseInfo(
    string Name,
    TargetSqlDatabaseState State,
    TargetSqlRecoveryModel RecoveryModel,
    bool IsSystemDatabase,
    bool IsSnapshot,
    bool CanBeManaged);

public sealed class TargetSqlDatabaseCatalog
{
    private readonly TargetSqlDatabaseInfo[] _databases;

    public TargetSqlDatabaseCatalog(IEnumerable<TargetSqlDatabaseInfo> databases)
    {
        ArgumentNullException.ThrowIfNull(databases);
        _databases = [.. databases];
        if (_databases.Any(database => database is null))
        {
            throw new ArgumentException("数据库目录不能包含空项。", nameof(databases));
        }
    }

    public IReadOnlyList<TargetSqlDatabaseInfo> Databases => [.. _databases];
}

public sealed record TargetSqlFullBackupCompletion(
    bool UsedCopyOnly,
    bool UsedChecksum,
    bool UsedCompression);

public sealed record TargetSqlBackupVerification(bool UsedChecksum);

public sealed record TargetSqlBackupIdentity(TargetSqlBackupIdentityStatus Status);

internal static class TargetSqlContractValues
{
    public static string ConnectionAddress(string? value, string parameterName)
    {
        var address = RequiredText(value, 255, parameterName);
        if (address.Contains(';', StringComparison.Ordinal)
            || address.Any(char.IsControl))
        {
            throw new ArgumentException(
                "连接地址必须是单一 Data Source，不能是完整连接字符串。",
                parameterName);
        }

        return address;
    }

    public static Guid RequiredId(Guid value, string parameterName)
    {
        return value != Guid.Empty
            ? value
            : throw new ArgumentException("标识不能为空。", parameterName);
    }

    public static string DatabaseName(string? value, string parameterName)
    {
        var databaseName = RequiredText(value, 128, parameterName);
        if (databaseName.Any(char.IsControl))
        {
            throw new ArgumentException("数据库名不能包含控制字符。", parameterName);
        }

        return databaseName;
    }

    public static int Positive(int value, int maximum, string parameterName)
    {
        return value is > 0 && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"值必须介于 1 和 {maximum} 之间。");
    }

    public static string RequiredText(string? value, int maximumLength, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"文本不能为空且长度不能超过 {maximumLength}。",
                parameterName);
        }

        return normalized;
    }

    public static string? OptionalSafeText(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"文本不能包含控制字符且长度不能超过 {maximumLength}。",
                parameterName);
        }

        return normalized;
    }

    public static string BackupFilePath(string? value, string parameterName)
    {
        var path = RequiredText(value, 2_048, parameterName);
        if (!path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "目标 SQL 本地备份路径必须使用 .bak 扩展名。",
                parameterName);
        }

        if (path.Any(char.IsControl))
        {
            throw new ArgumentException("目标 SQL 本地备份路径不能包含控制字符。", parameterName);
        }

        return path;
    }
}
