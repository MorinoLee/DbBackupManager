using System.Security.Claims;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.SqlCredentials;
using DbBackupManager.Web.Authentication;

namespace DbBackupManager.Web.SqlCredentials;

internal static class SqlCredentialPresentation
{
    public static AdminSession? GetActor(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && AdminPrincipalFactory.TryGetSessionClaims(principal, out var id, out var stamp)
            ? new AdminSession(id, principal.Identity.Name ?? string.Empty, stamp)
            : null;

    public static (int Status, string Code, string Message) Error(SqlCredentialResultCode code) => code switch
    {
        SqlCredentialResultCode.AuthenticationRequired => (401, "authentication_required", "登录状态已失效，请重新登录。"),
        SqlCredentialResultCode.ValidationFailed => (400, "validation_failed", "请检查名称、SQL 用户名、密码和版本信息。"),
        SqlCredentialResultCode.NotFound => (404, "credential_not_found", "该 SQL 凭据已不存在，请刷新列表。"),
        SqlCredentialResultCode.Conflict => (409, "credential_conflict", "名称已被使用，或凭据已被其他操作更新，请刷新后重试。"),
        SqlCredentialResultCode.ProtectionUnavailable => (503, "credential_protection_unavailable", "凭据加密暂不可用，请联系部署管理员检查密钥存储后重试。"),
        _ => (503, "credential_store_unavailable", "暂时无法访问凭据，请稍后重试。"),
    };
}
