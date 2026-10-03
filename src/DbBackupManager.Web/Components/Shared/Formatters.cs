namespace DbBackupManager.Web.Components.Shared;

/// <summary>
/// 全站统一的展示格式化：时间使用本地时区的完整时间戳，大小使用易读单位，空值显示“—”。
/// </summary>
internal static class Formatters
{
    public static string LocalDateTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "—";

    public static string Bytes(long? value)
    {
        if (value is null)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value.Value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value.Value} B"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size:0.##} {units[unit]}");
    }

    public static string Count(int value) =>
        value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
