namespace DbBackupManager.Web.Components.Shared;

internal static class AuthPageMessages
{
    public static string? ForError(string? errorCode)
    {
        return errorCode switch
        {
            "validation_failed" => "输入内容不符合要求。",
            "invalid_credentials" => "用户名或密码不正确。",
            "setup_unavailable" => "首次设置已完成，不能重复创建管理员。",
            "authentication_required" => "请先登录。",
            "concurrency_conflict" => "提交冲突，请刷新后重试。",
            "rate_limit_exceeded" => "请求过于频繁，请稍后重试。",
            _ => null,
        };
    }

    public static string? ForStatus(string? status)
    {
        return status switch
        {
            "password_changed" => "密码已修改，请使用新密码重新登录。",
            _ => null,
        };
    }

    public static string? SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.Contains('\\', StringComparison.Ordinal)
            || returnUrl.Contains(':', StringComparison.Ordinal)
            || returnUrl.Contains('\n', StringComparison.Ordinal)
            || returnUrl.Contains('\r', StringComparison.Ordinal))
        {
            return null;
        }

        return returnUrl;
    }
}
