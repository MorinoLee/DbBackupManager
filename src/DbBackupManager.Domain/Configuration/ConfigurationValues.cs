namespace DbBackupManager.Domain.Configuration;

internal static class ConfigurationValues
{
    public static Guid RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("标识不能为空。", parameterName);
        }

        return value;
    }

    public static string RequireText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"值的长度不能超过 {maximumLength} 个字符。");
        }

        return trimmed;
    }

    public static string? OptionalText(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RequireText(value, maximumLength, parameterName);
    }

    public static string Normalize(string value)
    {
        return value.ToUpperInvariant();
    }

    public static int RequirePositive(int value, int maximum, string parameterName)
    {
        if (value is < 1 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"值必须介于 1 与 {maximum} 之间。");
        }

        return value;
    }

    public static int? RequirePort(int? value, string parameterName)
    {
        if (value is null || value is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName, "端口必须介于 1 与 65535 之间。");
        }

        return value;
    }

    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("时间必须使用 UTC。", parameterName);
        }

        return value;
    }

    public static string RequireTimeZoneId(string value, string parameterName)
    {
        var timeZoneId = RequireText(value, 150, parameterName);

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException("时区标识不存在。", parameterName, exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException("时区配置无效。", parameterName, exception);
        }

        return timeZoneId;
    }

    public static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "枚举值无效。");
        }
    }

    public static string RequireEmail(string value, string parameterName)
    {
        var email = RequireText(value, 320, parameterName);
        var separator = email.IndexOf('@', StringComparison.Ordinal);
        if (separator <= 0
            || separator != email.LastIndexOf('@')
            || separator == email.Length - 1
            || email.Contains(' ', StringComparison.Ordinal)
            || !email[(separator + 1)..].Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("电子邮件地址格式无效。", parameterName);
        }

        return email;
    }
}
