using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Notifications;

public sealed class SmtpRecipient : ConcurrentEntity
{
    private SmtpRecipient()
    {
    }

    public SmtpRecipient(Guid id, Guid smtpSettingsId, string address)
        : base(id)
    {
        SmtpSettingsId = ConfigurationValues.RequireId(smtpSettingsId, nameof(smtpSettingsId));
        SetAddress(address);
    }

    public Guid SmtpSettingsId { get; private set; }

    public string Address { get; private set; } = string.Empty;

    public string NormalizedAddress { get; private set; } = string.Empty;

    private void SetAddress(string address)
    {
        Address = ConfigurationValues.RequireEmail(address, nameof(address));
        NormalizedAddress = ConfigurationValues.Normalize(Address);
    }
}
