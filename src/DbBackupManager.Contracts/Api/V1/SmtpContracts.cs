using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DbBackupManager.Contracts.Api.V1;

public enum SmtpSecurityModeValue
{
    StartTls = 1,
    TlsOnConnect = 2,
    Plaintext = 3
}

public sealed class SaveSmtpSettingsRequest
{
    [StringLength(16)]
    public string? Version { get; init; }

    [Required, StringLength(255)]
    public string Host { get; init; } = string.Empty;

    [JsonRequired, Range(1, 65535)]
    public int Port { get; init; }

    [JsonRequired, EnumDataType(typeof(SmtpSecurityModeValue))]
    public SmtpSecurityModeValue SecurityMode { get; init; }

    [Required, StringLength(320)]
    public string FromAddress { get; init; } = string.Empty;

    [JsonRequired, Range(1, 300)]
    public int TimeoutSeconds { get; init; }

    [JsonRequired]
    public bool IsEnabled { get; init; }
}

public sealed class SmtpVersionRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;
}

public sealed class SetSmtpEnabledRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [JsonRequired]
    public bool IsEnabled { get; init; }
}

public sealed class RotateSmtpPasswordRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [Required, StringLength(256)]
    public string Username { get; init; } = string.Empty;

    [Required, StringLength(1024)]
    public string Password { get; init; } = string.Empty;
}

public sealed class AddSmtpRecipientRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [Required, StringLength(320)]
    public string Address { get; init; } = string.Empty;
}

public sealed class RemoveSmtpRecipientRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [JsonRequired]
    public Guid RecipientId { get; init; }
}

public sealed class SendSmtpTestRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [JsonRequired]
    public Guid RequestId { get; init; }
}

public sealed record SmtpRecipientResponse(Guid Id, string Address);

public sealed record SmtpSettingsResponse(
    Guid Id,
    string Host,
    int Port,
    SmtpSecurityModeValue SecurityMode,
    string FromAddress,
    int TimeoutSeconds,
    bool IsEnabled,
    bool HasCredential,
    IReadOnlyList<SmtpRecipientResponse> Recipients,
    string Version);
