namespace DbBackupManager.Domain.Configuration;

public static class LegacySqlCompatibility
{
    public static string? Validate(bool enabled, string? reason)
    {
        var value = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (enabled != (value is not null) || value is { Length: > 500 }
            || value?.Any(char.IsControl) == true)
            throw new ArgumentException("旧 TLS 兼容例外启用时必须填写原因，关闭时必须清空原因。", nameof(reason));
        return value;
    }
}
