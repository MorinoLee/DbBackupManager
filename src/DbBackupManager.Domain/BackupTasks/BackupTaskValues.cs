namespace DbBackupManager.Domain.BackupTasks;

internal static class BackupTaskValues
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
        return string.IsNullOrWhiteSpace(value)
            ? null
            : RequireText(value, maximumLength, parameterName);
    }

    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("时间必须使用 UTC。", parameterName);
        }

        return value;
    }

    public static DateTimeOffset? OptionalUtc(DateTimeOffset? value, string parameterName)
    {
        return value is null ? null : RequireUtc(value.Value, parameterName);
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

    public static long RequirePositive(long value, string parameterName)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "值必须大于 0。");
        }

        return value;
    }

    public static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "枚举值无效。");
        }
    }
}
