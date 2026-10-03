namespace DbBackupManager.Web.Components.Layout;

/// <summary>
/// 导航分组与路由的对应关系。当前路由所属分组自动展开；
/// 新增路由时同步更新此处与 <c>NavMenu</c>。
/// </summary>
internal static class NavigationGroups
{
    public static bool IsDatabase(string path) => MatchesAny(path, "/servers", "/databases");

    public static bool IsBackup(string path) =>
        MatchesAny(path, "/backups", "/backup-policies", "/scheduled-backup-policies", "/storage-targets", "/backup-tasks", "/backup-files");

    public static bool IsSystem(string path) =>
        MatchesAny(path, "/sql-credentials", "/file-credentials", "/notifications", "/change-password");

    private static bool MatchesAny(string path, params string[] prefixes) =>
        prefixes.Any(prefix => path.Equals(prefix, StringComparison.Ordinal)
            || path.StartsWith($"{prefix}/", StringComparison.Ordinal));
}
