using System.Security.Claims;
using DbBackupManager.Application.FileCredentials;
using DbBackupManager.Application.Identity;
using DbBackupManager.Web.Authentication;

namespace DbBackupManager.Web.FileCredentials;

internal static class FileCredentialPresentation
{
    public static AdminSession? GetActor(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && AdminPrincipalFactory.TryGetSessionClaims(principal, out var id, out var stamp)
            ? new AdminSession(id, principal.Identity.Name ?? string.Empty, stamp)
            : null;

    public static string Kind(FileCredentialKind kind) => kind switch
    {
        FileCredentialKind.SmbPassword => "SMB",
        FileCredentialKind.SftpPassword => "SFTP 密码",
        FileCredentialKind.SftpPrivateKey => "SFTP 私钥",
        _ => "未知类型",
    };

    public static (int Status, string Code, string Message) Error(FileCredentialResultCode code) => code switch
    {
        FileCredentialResultCode.AuthenticationRequired => (401, "authentication_required", "登录状态已失效，请重新登录。"),
        FileCredentialResultCode.ValidationFailed => (400, "validation_failed", "请检查名称、文件访问用户名、密码、私钥和版本信息。"),
        FileCredentialResultCode.NotFound => (404, "credential_not_found", "该 暂存访问凭据已不存在，请刷新列表。"),
        FileCredentialResultCode.Conflict => (409, "credential_conflict", "名称已被使用，或凭据已被其他操作更新，请刷新后重试。"),
        FileCredentialResultCode.ProtectionUnavailable => (503, "credential_protection_unavailable", "凭据加密暂不可用，请联系部署管理员检查密钥存储后重试。"),
        _ => (503, "credential_store_unavailable", "暂时无法访问凭据，请稍后重试。"),
    };
}
