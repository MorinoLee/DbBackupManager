namespace DbBackupManager.Contracts.Api.V1;

public sealed record SystemVersionResponse(
    string Product,
    string ApiVersion,
    string ApplicationVersion);
