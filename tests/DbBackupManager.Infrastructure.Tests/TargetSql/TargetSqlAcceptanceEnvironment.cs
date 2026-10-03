using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

internal enum TargetSqlAcceptanceProfile
{
    SqlServer2008R2,
    SqlServer2019,
}

internal sealed class TargetSqlAcceptanceEnvironment
{
    public const string ConnectionVariable = "DBBACKUPMANAGER_TEST_TARGET_SQL_CONNECTION";
    public const string DatabaseVariable = "DBBACKUPMANAGER_TEST_TARGET_DATABASE";
    public const string SqlBackupRootVariable = "DBBACKUPMANAGER_TEST_TARGET_SQL_BACKUP_ROOT";
    public const string WorkerBackupRootVariable = "DBBACKUPMANAGER_TEST_TARGET_WORKER_BACKUP_ROOT";
    public const string ExpectedProfileVariable = "DBBACKUPMANAGER_TEST_TARGET_EXPECTED_PROFILE";
    public const string AllowMutationVariable = "DBBACKUPMANAGER_TEST_TARGET_ALLOW_MUTATION";
    public const string AllowCertificateTrustVariable =
        "DBBACKUPMANAGER_TEST_TARGET_ALLOW_CERTIFICATE_TRUST";
    public const string CertificateTrustReasonVariable =
        "DBBACKUPMANAGER_TEST_TARGET_CERTIFICATE_TRUST_REASON";

    public const string TargetDatabasePrefix = "DbBackupManagerP56Target_";
    public const string BackupFilePrefix = "DbBackupManagerP56Backup_";
    public const string RestoreDatabasePrefix = "DbBackupManagerP56Restore_";
    public const string RestoreFilePrefix = "DbBackupManagerP56RestoreFile_";
    public const string ControlledRootPrefix = "DbBackupManagerP56";

    private static readonly string[] RequiredVariables =
    [
        ConnectionVariable,
        DatabaseVariable,
        SqlBackupRootVariable,
        WorkerBackupRootVariable,
        ExpectedProfileVariable,
    ];

    private readonly Func<string, string?> _valueReader;

    private TargetSqlAcceptanceEnvironment(
        Func<string, string?> valueReader,
        TargetSqlAcceptanceProfile profile,
        string connectionAddress,
        string databaseName,
        string sqlBackupRoot,
        string workerBackupRoot,
        bool trustServerCertificate,
        string? certificateTrustReason,
        bool allowMutation)
    {
        _valueReader = valueReader;
        Profile = profile;
        ConnectionAddress = connectionAddress;
        DatabaseName = databaseName;
        SqlBackupRoot = sqlBackupRoot;
        WorkerBackupRoot = workerBackupRoot;
        TrustServerCertificate = trustServerCertificate;
        CertificateTrustReason = certificateTrustReason;
        AllowMutation = allowMutation;
    }

    public TargetSqlAcceptanceProfile Profile { get; }

    public string ConnectionAddress { get; }

    public string DatabaseName { get; }

    public string SqlBackupRoot { get; }

    public string WorkerBackupRoot { get; }

    public bool TrustServerCertificate { get; }

    public string? CertificateTrustReason { get; }

    public bool AllowMutation { get; }

    public static TargetSqlAcceptanceEnvironment? LoadFromProcess()
    {
        return Load(Environment.GetEnvironmentVariable);
    }

    internal static TargetSqlAcceptanceEnvironment? Load(Func<string, string?> valueReader)
    {
        ArgumentNullException.ThrowIfNull(valueReader);

        var relevantVariables = RequiredVariables
            .Append(AllowMutationVariable)
            .Append(AllowCertificateTrustVariable)
            .Append(CertificateTrustReasonVariable)
            .ToArray();
        if (relevantVariables.All(variable => string.IsNullOrWhiteSpace(valueReader(variable))))
        {
            return null;
        }

        var missing = RequiredVariables
            .Where(variable => string.IsNullOrWhiteSpace(valueReader(variable)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"P5.6 真实目标配置不完整，缺少键：{string.Join(", ", missing)}。未执行任何目标操作。");
        }

        var profile = ParseProfile(valueReader(ExpectedProfileVariable)!);
        var allowMutation = ParseOptionalBoolean(valueReader(AllowMutationVariable), AllowMutationVariable);
        var allowCertificateTrust = ParseOptionalBoolean(
            valueReader(AllowCertificateTrustVariable),
            AllowCertificateTrustVariable);
        var trustReason = NormalizeOptional(valueReader(CertificateTrustReasonVariable));
        var databaseName = RequireControlledDatabase(valueReader(DatabaseVariable)!);
        var sqlBackupRoot = RequireControlledRoot(
            valueReader(SqlBackupRootVariable)!,
            SqlBackupRootVariable,
            requireExisting: false);
        var workerBackupRoot = RequireControlledRoot(
            valueReader(WorkerBackupRootVariable)!,
            WorkerBackupRootVariable,
            requireExisting: true);
        var connection = ParseConnection(
            valueReader(ConnectionVariable)!,
            databaseName,
            profile,
            allowCertificateTrust,
            trustReason);
        var connectionAddress = connection.DataSource;
        var trustServerCertificate = connection.TrustServerCertificate;
        connection.Password = string.Empty;

        return new TargetSqlAcceptanceEnvironment(
            valueReader,
            profile,
            connectionAddress,
            databaseName,
            sqlBackupRoot,
            workerBackupRoot,
            trustServerCertificate,
            trustReason,
            allowMutation);
    }

    public TargetSqlConnectionInput CreateConnectionInput(Guid credentialReferenceId)
    {
        return new TargetSqlConnectionInput(
            ConnectionAddress,
            credentialReferenceId,
            encryptConnection: true,
            TrustServerCertificate,
            CertificateTrustReason,
            connectionTimeoutSeconds: 30);
    }

    public TargetSqlCredentialLease CreateCredentialLease()
    {
        var rawConnection = _valueReader(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            throw new InvalidOperationException("P5.6 目标 SQL 凭据在执行时不可用。");
        }

        var builder = ParseConnection(
            rawConnection,
            DatabaseName,
            Profile,
            TrustServerCertificate,
            CertificateTrustReason);
        var password = builder.Password.ToCharArray();
        var userName = builder.UserID;
        builder.Password = string.Empty;
        return TargetSqlCredentialLease.CreateAndClear(userName, password);
    }

    public void ValidateServer(TargetSqlServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!Version.TryParse(server.ProductVersion, out var version))
        {
            throw new InvalidOperationException("P5.6 目标返回了无法解析的产品版本。");
        }

        var matches = Profile switch
        {
            TargetSqlAcceptanceProfile.SqlServer2008R2 =>
                version.Major == 10
                && version.Minor == 50
                && version.Build >= 6_542
                && string.Equals(server.ProductLevel, "SP3", StringComparison.OrdinalIgnoreCase),
            TargetSqlAcceptanceProfile.SqlServer2019 => version.Major == 15,
            _ => false,
        };
        if (!matches)
        {
            throw new InvalidOperationException(
                $"P5.6 目标版本与预期配置 {Profile} 不符；实际产品版本 {server.ProductVersion}、级别 {server.ProductLevel ?? "未提供"}。未执行写操作。");
        }
    }

    public (string SqlPath, string WorkerPath) CreateBackupPaths(Guid runId)
    {
        var fileName = $"{BackupFilePrefix}{runId:N}.bak";
        return (
            Path.Combine(SqlBackupRoot, fileName),
            Path.Combine(WorkerBackupRoot, fileName));
    }

    public (string SqlPath, string WorkerPath) CreateRestoreFilePaths(
        Guid runId,
        int index,
        char fileType)
    {
        var extension = fileType switch
        {
            'D' when index == 0 => ".mdf",
            'D' => ".ndf",
            'L' => ".ldf",
            _ => throw new InvalidOperationException(
                "P5.6 隔离 Restore 仅接受普通数据文件和日志文件。"),
        };
        var fileName = $"{RestoreFilePrefix}{runId:N}_{index:D2}{extension}";
        return (
            Path.Combine(SqlBackupRoot, fileName),
            Path.Combine(WorkerBackupRoot, fileName));
    }

    public static void EnsureRestoreDatabaseName(string databaseName)
    {
        if (!databaseName.StartsWith(RestoreDatabasePrefix, StringComparison.Ordinal)
            || databaseName.Length != RestoreDatabasePrefix.Length + 32)
        {
            throw new InvalidOperationException("拒绝操作不属于本轮 P5.6 的隔离 Restore 数据库。");
        }
    }

    public void DeleteGeneratedFile(string workerPath)
    {
        var normalizedPath = Path.GetFullPath(workerPath);
        var parent = Path.GetDirectoryName(normalizedPath);
        var fileName = Path.GetFileName(normalizedPath);
        if (!string.Equals(parent, WorkerBackupRoot, StringComparison.OrdinalIgnoreCase)
            || (!fileName.StartsWith(BackupFilePrefix, StringComparison.Ordinal)
                && !fileName.StartsWith(RestoreFilePrefix, StringComparison.Ordinal))
            || Path.GetExtension(fileName) is not (".bak" or ".mdf" or ".ndf" or ".ldf"))
        {
            throw new InvalidOperationException("拒绝删除不属于本轮 P5.6 的文件。");
        }

        if (File.Exists(normalizedPath))
        {
            if (File.GetAttributes(normalizedPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("拒绝删除 P5.6 重解析点文件。");
            }

            File.Delete(normalizedPath);
        }
    }

    private static SqlConnectionStringBuilder ParseConnection(
        string rawConnection,
        string databaseName,
        TargetSqlAcceptanceProfile profile,
        bool allowCertificateTrust,
        string? trustReason)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(rawConnection);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "P5.6 目标连接配置无法解析；未输出配置值且未执行目标操作。");
        }

        if (builder.IntegratedSecurity
            || string.IsNullOrWhiteSpace(builder.UserID)
            || string.IsNullOrEmpty(builder.Password))
        {
            throw new InvalidOperationException("P5.6 真实矩阵必须使用 SQL Password 身份。");
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.DataSource.Contains(';', StringComparison.Ordinal)
            || builder.DataSource.Any(char.IsControl))
        {
            throw new InvalidOperationException("P5.6 目标连接缺少安全的单一服务器地址。");
        }

        if (builder.Encrypt is null || builder.Encrypt == SqlConnectionEncryptOption.Optional)
        {
            throw new InvalidOperationException("P5.6 目标连接必须显式启用加密。");
        }

        if (profile == TargetSqlAcceptanceProfile.SqlServer2008R2
            && builder.Encrypt == SqlConnectionEncryptOption.Strict)
        {
            throw new InvalidOperationException("SQL Server 2008 R2 矩阵不能使用仅适用于 TDS 8.0 的 Strict 加密模式。");
        }

        if (builder.TrustServerCertificate != allowCertificateTrust)
        {
            throw new InvalidOperationException(
                "P5.6 证书信任配置与显式受控例外开关不一致。");
        }

        if (builder.TrustServerCertificate != (trustReason is not null))
        {
            throw new InvalidOperationException(
                "P5.6 证书信任例外必须同时提供开关与非空原因，严格验证模式不得提供例外原因。");
        }

        if (!string.IsNullOrWhiteSpace(builder.InitialCatalog)
            && !string.Equals(builder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(builder.InitialCatalog, databaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "P5.6 连接配置中的数据库与专用目标数据库键不一致。");
        }

        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename) || builder.UserInstance)
        {
            throw new InvalidOperationException("P5.6 真实目标不允许附加数据库文件或 User Instance。");
        }

        builder.InitialCatalog = "master";
        builder.Pooling = false;
        builder.PersistSecurityInfo = false;
        builder.Enlist = false;
        builder.ConnectRetryCount = 0;
        builder.MultipleActiveResultSets = false;
        return builder;
    }

    private static TargetSqlAcceptanceProfile ParseProfile(string value)
    {
        return value.Trim() switch
        {
            "SqlServer2008R2" => TargetSqlAcceptanceProfile.SqlServer2008R2,
            "SqlServer2019" => TargetSqlAcceptanceProfile.SqlServer2019,
            _ => throw new InvalidOperationException(
                $"{ExpectedProfileVariable} 只允许 SqlServer2008R2 或 SqlServer2019。"),
        };
    }

    private static bool ParseOptionalBoolean(string? value, string variableName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{variableName} 只允许 true 或 false。");
    }

    private static string RequireControlledDatabase(string value)
    {
        var databaseName = value.Trim();
        if (!databaseName.StartsWith(TargetDatabasePrefix, StringComparison.Ordinal)
            || databaseName.Length > 128
            || databaseName.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"P5.6 真实矩阵只允许 {TargetDatabasePrefix} 前缀的专用源数据库。");
        }

        return databaseName;
    }

    private static string RequireControlledRoot(
        string value,
        string variableName,
        bool requireExisting)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value.Trim()))
        {
            throw new InvalidOperationException($"{variableName} 必须是绝对目录。");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
        var directory = new DirectoryInfo(root);
        if (directory.Parent is null
            || !directory.Name.StartsWith(ControlledRootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{variableName} 必须指向叶目录名以 {ControlledRootPrefix} 开头的专用目录，不能是磁盘或共享根。");
        }

        if (requireExisting && !directory.Exists)
        {
            throw new InvalidOperationException(
                $"{WorkerBackupRootVariable} 指向的 Worker 可见专用目录不存在；夹具不会自动创建。");
        }

        if (requireExisting && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"{WorkerBackupRootVariable} 不允许指向重解析点或符号链接。");
        }

        return root;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > 500 || normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException("P5.6 证书信任例外原因格式无效。");
        }

        return normalized;
    }
}

internal sealed class AcceptanceEnvironmentCredentialResolver(
    TargetSqlAcceptanceEnvironment environment,
    Guid credentialReferenceId) : ITargetSqlCredentialResolver
{
    public ValueTask<TargetSqlCredentialResolution> ResolveSqlPasswordAsync(
        Guid requestedCredentialReferenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestedCredentialReferenceId != credentialReferenceId)
        {
            return ValueTask.FromResult(TargetSqlCredentialResolution.Failed(
                TargetSqlFailureCode.CredentialUnavailable));
        }

        try
        {
            return ValueTask.FromResult(TargetSqlCredentialResolution.Succeeded(
                environment.CreateCredentialLease()));
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(TargetSqlCredentialResolution.Failed(
                TargetSqlFailureCode.CredentialUnavailable));
        }
    }
}
