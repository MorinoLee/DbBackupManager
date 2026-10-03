using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Configuration;

public sealed class DatabaseServer : ConcurrentEntity
{
    private DatabaseServer()
    {
    }

    public DatabaseServer(
        Guid id,
        string name,
        string localBackupRootPath,
        FileEndpointSettings stagingAccess,
        string? description = null,
        bool isEnabled = true)
        : base(id)
    {
        SetConfiguration(name, localBackupRootPath, stagingAccess, description);
        IsEnabled = isEnabled;
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string LocalBackupRootPath { get; private set; } = string.Empty;

    public FileTransferProtocol StagingAccessProtocol { get; private set; }

    public string StagingAccessHost { get; private set; } = string.Empty;

    public int? StagingAccessPort { get; private set; }

    public string StagingAccessBasePath { get; private set; } = string.Empty;

    public Guid StagingCredentialReferenceId { get; private set; }

    public string? StagingSftpHostKeyFingerprint { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Update(
        string name,
        string localBackupRootPath,
        FileEndpointSettings stagingAccess,
        string? description)
    {
        SetConfiguration(name, localBackupRootPath, stagingAccess, description);
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    private void SetConfiguration(
        string name,
        string localBackupRootPath,
        FileEndpointSettings stagingAccess,
        string? description)
    {
        var validatedName = ConfigurationValues.RequireText(name, 200, nameof(name));
        var validatedRootPath = ConfigurationValues.RequireText(
            localBackupRootPath,
            2048,
            nameof(localBackupRootPath));
        var validatedDescription = ConfigurationValues.OptionalText(
            description,
            1000,
            nameof(description));
        var endpoint = stagingAccess.Validate(nameof(stagingAccess));

        Name = validatedName;
        NormalizedName = ConfigurationValues.Normalize(validatedName);
        LocalBackupRootPath = validatedRootPath;
        Description = validatedDescription;
        StagingAccessProtocol = endpoint.Protocol;
        StagingAccessHost = endpoint.Host;
        StagingAccessPort = endpoint.Port;
        StagingAccessBasePath = endpoint.BasePath;
        StagingCredentialReferenceId = endpoint.CredentialReferenceId;
        StagingSftpHostKeyFingerprint = endpoint.SftpHostKeyFingerprint;
    }
}
