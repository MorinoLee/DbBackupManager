using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Configuration;

public sealed class CredentialReference : ConcurrentEntity
{
    private const int MaximumProtectedValueLength = 65_535;

    private CredentialReference()
    {
    }

    public CredentialReference(
        Guid id,
        string name,
        CredentialKind kind,
        string username,
        string protectedSecret,
        string protectionVersion,
        string? protectedSecondarySecret = null,
        bool isEnabled = true)
        : base(id)
    {
        ConfigurationValues.RequireDefined(kind, nameof(kind));
        Kind = kind;
        SetName(name);
        Username = ConfigurationValues.RequireText(username, 256, nameof(username));
        ProtectionVersion = ConfigurationValues.RequireText(
            protectionVersion,
            50,
            nameof(protectionVersion));
        SetProtectedValues(protectedSecret, protectedSecondarySecret);
        IsEnabled = isEnabled;
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public CredentialKind Kind { get; private set; }

    public string Username { get; private set; } = string.Empty;

    public string ProtectedSecret { get; private set; } = string.Empty;

    public string? ProtectedSecondarySecret { get; private set; }

    public string ProtectionVersion { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public void Rename(string name)
    {
        SetName(name);
    }

    public void RotateProtectedValues(
        string protectedSecret,
        string protectionVersion,
        string? protectedSecondarySecret = null)
    {
        var validatedVersion = ConfigurationValues.RequireText(
            protectionVersion,
            50,
            nameof(protectionVersion));
        var validatedValues = ValidateProtectedValues(protectedSecret, protectedSecondarySecret);

        ProtectionVersion = validatedVersion;
        ProtectedSecret = validatedValues.Primary;
        ProtectedSecondarySecret = validatedValues.Secondary;
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

    private void SetProtectedValues(string protectedSecret, string? protectedSecondarySecret)
    {
        var validatedValues = ValidateProtectedValues(protectedSecret, protectedSecondarySecret);
        ProtectedSecret = validatedValues.Primary;
        ProtectedSecondarySecret = validatedValues.Secondary;
    }

    private (string Primary, string? Secondary) ValidateProtectedValues(
        string protectedSecret,
        string? protectedSecondarySecret)
    {
        var primary = ConfigurationValues.RequireText(
            protectedSecret,
            MaximumProtectedValueLength,
            nameof(protectedSecret));
        var secondary = ConfigurationValues.OptionalText(
            protectedSecondarySecret,
            MaximumProtectedValueLength,
            nameof(protectedSecondarySecret));

        if (Kind != CredentialKind.SftpPrivateKey && secondary is not null)
        {
            throw new ArgumentException(
                "只有 SFTP 私钥凭据可以保存受保护的第二秘密。",
                nameof(protectedSecondarySecret));
        }

        return (primary, secondary);
    }
}
