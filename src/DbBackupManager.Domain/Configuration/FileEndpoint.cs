namespace DbBackupManager.Domain.Configuration;

public sealed record FileEndpointSettings(
    FileTransferProtocol Protocol,
    string Host,
    int? Port,
    string BasePath,
    Guid CredentialReferenceId,
    string? SftpHostKeyFingerprint)
{
    internal FileEndpointSettings Validate(string parameterName)
    {
        ConfigurationValues.RequireDefined(Protocol, $"{parameterName}.{nameof(Protocol)}");
        var host = ConfigurationValues.RequireText(Host, 255, $"{parameterName}.{nameof(Host)}");
        var basePath = ConfigurationValues.RequireText(
            BasePath,
            2048,
            $"{parameterName}.{nameof(BasePath)}");
        var credentialReferenceId = ConfigurationValues.RequireId(
            CredentialReferenceId,
            $"{parameterName}.{nameof(CredentialReferenceId)}");

        return Protocol switch
        {
            FileTransferProtocol.Smb when Port is not null => throw new ArgumentException(
                "SMB 端点不接受自定义端口。",
                parameterName),
            FileTransferProtocol.Smb when !string.IsNullOrWhiteSpace(SftpHostKeyFingerprint) =>
                throw new ArgumentException("SMB 端点不能配置 SFTP Host Key 指纹。", parameterName),
            FileTransferProtocol.Smb => this with
            {
                Host = host,
                BasePath = basePath,
                CredentialReferenceId = credentialReferenceId,
                SftpHostKeyFingerprint = null
            },
            FileTransferProtocol.Sftp => this with
            {
                Host = host,
                Port = ConfigurationValues.RequirePort(Port, $"{parameterName}.{nameof(Port)}"),
                BasePath = basePath,
                CredentialReferenceId = credentialReferenceId,
                SftpHostKeyFingerprint = ConfigurationValues.RequireText(
                    SftpHostKeyFingerprint!,
                    500,
                    $"{parameterName}.{nameof(SftpHostKeyFingerprint)}")
            },
            _ => throw new ArgumentOutOfRangeException(parameterName)
        };
    }
}
