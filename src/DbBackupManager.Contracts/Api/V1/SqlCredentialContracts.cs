using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DbBackupManager.Contracts.Api.V1;

public sealed class CreateSqlCredentialRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(128)]
    public string Username { get; init; } = string.Empty;

    [Required, StringLength(128)]
    public string Password { get; init; } = string.Empty;
}

public sealed class RotateSqlCredentialPasswordRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [Required, StringLength(128)]
    public string Password { get; init; } = string.Empty;
}

public sealed class SetSqlCredentialEnabledRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [JsonRequired]
    public bool IsEnabled { get; init; }
}

public sealed record SqlCredentialResponse(
    Guid Id, string Name, string Username, bool IsEnabled, string Version);
