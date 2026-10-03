using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Configuration;

public sealed class DatabaseInstance : ConcurrentEntity
{
    private DatabaseInstance()
    {
    }

    public DatabaseInstance(
        Guid id,
        Guid serverId,
        string name,
        string connectionAddress,
        Guid sqlCredentialReferenceId,
        bool encryptConnection,
        bool trustServerCertificate,
        string? certificateTrustReason,
        int connectionTimeoutSeconds,
        bool isEnabled = true,
        bool allowLegacyTls = false, string? legacyTlsReason = null)
        : base(id)
    {
        ServerId = ConfigurationValues.RequireId(serverId, nameof(serverId));
        SetName(name);
        SetConnection(
            connectionAddress,
            sqlCredentialReferenceId,
            encryptConnection,
            trustServerCertificate,
            certificateTrustReason,
            connectionTimeoutSeconds, allowLegacyTls, legacyTlsReason);
        IsEnabled = isEnabled;
        ConnectionStatus = SqlConnectionStatus.Unknown;
    }

    public Guid ServerId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public string ConnectionAddress { get; private set; } = string.Empty;

    public string NormalizedConnectionAddress { get; private set; } = string.Empty;

    public Guid SqlCredentialReferenceId { get; private set; }

    public bool EncryptConnection { get; private set; }

    public bool TrustServerCertificate { get; private set; }

    public string? CertificateTrustReason { get; private set; }

    public bool AllowLegacyTls { get; private set; }

    public string? LegacyTlsReason { get; private set; }

    public int ConnectionTimeoutSeconds { get; private set; }

    public SqlConnectionStatus ConnectionStatus { get; private set; }

    public string? ProductVersion { get; private set; }

    public string? ProductLevel { get; private set; }

    public string? Edition { get; private set; }

    public DateTimeOffset? LastConnectedAtUtc { get; private set; }

    public DateTimeOffset? LastConnectionCheckedAtUtc { get; private set; }

    public string? LastConnectionErrorCode { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Rename(string name)
    {
        SetName(name);
    }

    public void UpdateConnection(
        string connectionAddress,
        Guid sqlCredentialReferenceId,
        bool encryptConnection,
        bool trustServerCertificate,
        string? certificateTrustReason,
        int connectionTimeoutSeconds,
        bool allowLegacyTls = false, string? legacyTlsReason = null)
    {
        SetConnection(
            connectionAddress,
            sqlCredentialReferenceId,
            encryptConnection,
            trustServerCertificate,
            certificateTrustReason,
            connectionTimeoutSeconds, allowLegacyTls, legacyTlsReason);
        ResetProbeState();
    }

    public void RecordConnectionSucceeded(
        DateTimeOffset connectedAtUtc,
        string productVersion,
        string? productLevel,
        string? edition)
    {
        var validatedAtUtc = ConfigurationValues.RequireUtc(connectedAtUtc, nameof(connectedAtUtc));
        var validatedVersion = ConfigurationValues.RequireText(
            productVersion,
            100,
            nameof(productVersion));
        var validatedLevel = ConfigurationValues.OptionalText(productLevel, 100, nameof(productLevel));
        var validatedEdition = ConfigurationValues.OptionalText(edition, 300, nameof(edition));

        ProductVersion = validatedVersion;
        ProductLevel = validatedLevel;
        Edition = validatedEdition;
        LastConnectedAtUtc = validatedAtUtc;
        LastConnectionCheckedAtUtc = validatedAtUtc;
        LastConnectionErrorCode = null;
        ConnectionStatus = SqlConnectionStatus.Connected;
    }

    public void RecordConnectionFailed(DateTimeOffset checkedAtUtc, string errorCode)
    {
        var validatedAtUtc = ConfigurationValues.RequireUtc(checkedAtUtc, nameof(checkedAtUtc));
        var validatedErrorCode = ConfigurationValues.RequireText(errorCode, 100, nameof(errorCode));

        LastConnectionCheckedAtUtc = validatedAtUtc;
        LastConnectionErrorCode = validatedErrorCode;
        ConnectionStatus = SqlConnectionStatus.Failed;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    private void SetName(string name)
    {
        Name = ConfigurationValues.RequireText(name, 200, nameof(name));
        NormalizedName = ConfigurationValues.Normalize(Name);
    }

    private void SetConnection(
        string connectionAddress,
        Guid sqlCredentialReferenceId,
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

        var trustReason = ConfigurationValues.OptionalText(
            certificateTrustReason,
            500,
            nameof(certificateTrustReason));
        if (trustServerCertificate && trustReason is null)
        {
            throw new ArgumentException(
                "显式信任服务器证书时必须填写受控例外原因。",
                nameof(certificateTrustReason));
        }

        if (!trustServerCertificate && trustReason is not null)
        {
            throw new ArgumentException(
                "未启用证书信任例外时不能保存例外原因。",
                nameof(certificateTrustReason));
        }

        var validatedAddress = ConfigurationValues.RequireText(
            connectionAddress,
            255,
            nameof(connectionAddress));
        var validatedCredentialId = ConfigurationValues.RequireId(
            sqlCredentialReferenceId,
            nameof(sqlCredentialReferenceId));
        var validatedTimeout = ConfigurationValues.RequirePositive(
            connectionTimeoutSeconds,
            300,
            nameof(connectionTimeoutSeconds));

        var legacyReason = LegacySqlCompatibility.Validate(allowLegacyTls, legacyTlsReason);
        AllowLegacyTls = allowLegacyTls;
        LegacyTlsReason = legacyReason;
        ConnectionAddress = validatedAddress;
        NormalizedConnectionAddress = ConfigurationValues.Normalize(validatedAddress);
        SqlCredentialReferenceId = validatedCredentialId;
        EncryptConnection = true;
        TrustServerCertificate = trustServerCertificate;
        CertificateTrustReason = trustReason;
        ConnectionTimeoutSeconds = validatedTimeout;
    }

    private void ResetProbeState()
    {
        ProductVersion = null;
        ProductLevel = null;
        Edition = null;
        LastConnectedAtUtc = null;
        LastConnectionCheckedAtUtc = null;
        LastConnectionErrorCode = null;
        ConnectionStatus = SqlConnectionStatus.Unknown;
    }
}
