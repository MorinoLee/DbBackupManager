using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Configuration;

public sealed class StorageTarget : ConcurrentEntity
{
    private StorageTarget()
    {
    }

    public StorageTarget(
        Guid id,
        string name,
        FileEndpointSettings endpoint,
        bool isEnabled = true)
        : base(id)
    {
        SetName(name);
        SetEndpoint(endpoint);
        IsEnabled = isEnabled;
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public FileTransferProtocol Protocol { get; private set; }

    public string Host { get; private set; } = string.Empty;

    public int? Port { get; private set; }

    public string BasePath { get; private set; } = string.Empty;

    public Guid CredentialReferenceId { get; private set; }

    public string? SftpHostKeyFingerprint { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Rename(string name)
    {
        SetName(name);
    }

    public void UpdateEndpoint(FileEndpointSettings endpoint)
    {
        SetEndpoint(endpoint);
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

    private void SetEndpoint(FileEndpointSettings endpoint)
    {
        var validated = endpoint.Validate(nameof(endpoint));
        Protocol = validated.Protocol;
        Host = validated.Host;
        Port = validated.Port;
        BasePath = validated.BasePath;
        CredentialReferenceId = validated.CredentialReferenceId;
        SftpHostKeyFingerprint = validated.SftpHostKeyFingerprint;
    }
}
