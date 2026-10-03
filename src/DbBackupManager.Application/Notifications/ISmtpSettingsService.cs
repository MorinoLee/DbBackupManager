using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;

namespace DbBackupManager.Application.Notifications;

public sealed record SmtpRecipientItem(Guid Id, string Address);

public sealed record SmtpSettingsItem(
    Guid Id,
    string Host,
    int Port,
    int SecurityMode,
    string FromAddress,
    int TimeoutSeconds,
    bool IsEnabled,
    bool HasCredential,
    IReadOnlyList<SmtpRecipientItem> Recipients,
    string Version);

public sealed record SmtpSettingsInput(
    string Host,
    int Port,
    int SecurityMode,
    string FromAddress,
    int TimeoutSeconds,
    bool IsEnabled);

public interface ISmtpSettingsService
{
    Task<ManagementResult<SmtpSettingsItem>> GetAsync(
        AdminSession actor,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<SmtpSettingsItem>> SaveAsync(
        AdminSession actor,
        string? version,
        SmtpSettingsInput input,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<SmtpSettingsItem>> SetEnabledAsync(
        AdminSession actor,
        string version,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<SmtpSettingsItem>> RotatePasswordAsync(
        AdminSession actor,
        string version,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<SmtpSettingsItem>> ClearPasswordAsync(
        AdminSession actor,
        string version,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<SmtpSettingsItem>> AddRecipientAsync(
        AdminSession actor,
        string version,
        string address,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<SmtpSettingsItem>> RemoveRecipientAsync(
        AdminSession actor,
        string version,
        Guid recipientId,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<SmtpSettingsItem>> QueueTestAsync(
        AdminSession actor,
        string version,
        Guid requestId,
        CancellationToken cancellationToken = default);
}
