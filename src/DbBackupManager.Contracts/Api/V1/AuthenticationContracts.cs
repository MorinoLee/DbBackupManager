namespace DbBackupManager.Contracts.Api.V1;

public sealed record CsrfTokenResponse(string Token);

public sealed record SetupStatusResponse(bool RequiresSetup);

public sealed record AdminSessionResponse(Guid AdminUserId, string Username);

public sealed record AdminSetupRequest(string? Username, string? Password);

public sealed record AdminLoginRequest(string? Username, string? Password);

public sealed record AdminChangePasswordRequest(string? CurrentPassword, string? NewPassword);
