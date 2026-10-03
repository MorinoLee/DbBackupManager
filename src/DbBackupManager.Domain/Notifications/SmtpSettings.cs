using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Notifications;

public sealed class SmtpSettings : ConcurrentEntity
{
    public static Guid SingletonId { get; } = Guid.Parse("8f3c2a10-5d6e-4b91-9c7a-11d000000001");

    private SmtpSettings()
    {
    }

    public SmtpSettings(
        string host,
        int port,
        SmtpSecurityMode securityMode,
        string fromAddress,
        int timeoutSeconds,
        bool isEnabled)
        : base(SingletonId)
    {
        Replace(host, port, securityMode, fromAddress, timeoutSeconds, isEnabled);
    }

    public string Host { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public SmtpSecurityMode SecurityMode { get; private set; }

    public Guid? CredentialReferenceId { get; private set; }

    public string FromAddress { get; private set; } = string.Empty;

    public int TimeoutSeconds { get; private set; }

    public bool IsEnabled { get; private set; }

    public int ConfigurationSerial { get; private set; }

    public void Replace(
        string host,
        int port,
        SmtpSecurityMode securityMode,
        string fromAddress,
        int timeoutSeconds,
        bool isEnabled)
    {
        ConfigurationValues.RequireDefined(securityMode, nameof(securityMode));
        Host = ConfigurationValues.RequireText(host, 255, nameof(host));
        Port = ConfigurationValues.RequirePositive(port, 65_535, nameof(port));
        SecurityMode = securityMode;
        FromAddress = ConfigurationValues.RequireEmail(fromAddress, nameof(fromAddress));
        TimeoutSeconds = ConfigurationValues.RequirePositive(timeoutSeconds, 300, nameof(timeoutSeconds));
        IsEnabled = isEnabled;
        NoteMutation();
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
        NoteMutation();
    }

    public void AssignCredential(Guid credentialReferenceId)
    {
        CredentialReferenceId = ConfigurationValues.RequireId(credentialReferenceId, nameof(credentialReferenceId));
        NoteMutation();
    }

    public void ClearCredential()
    {
        CredentialReferenceId = null;
        NoteMutation();
    }

    public void MarkRecipientsChanged() => NoteMutation();

    private void NoteMutation() => ConfigurationSerial = checked(ConfigurationSerial + 1);
}
