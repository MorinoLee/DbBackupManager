using MudBlazor;

namespace DbBackupManager.Web.Components.Layout;

/// <summary>
/// 管理界面的品牌主题。
/// </summary>
/// <remarks>
/// 实例在所有 Circuit 之间共享，运行时不得修改其中的调色板或布局属性。
/// 亮色与深色调色板的正文、次要文字和状态色都按 WCAG AA 正文对比度挑选，
/// 后续调整颜色时必须重新核对对比度。
/// </remarks>
internal static class AppTheme
{
    public static MudTheme Default { get; } = Create();

    private static MudTheme Create()
    {
        return new MudTheme
        {
            PaletteLight = new PaletteLight
            {
                Primary = "#1B5FA8",
                PrimaryContrastText = "#FFFFFF",
                Secondary = "#00697A",
                SecondaryContrastText = "#FFFFFF",
                Info = "#0277BD",
                Success = "#2E7D32",
                Warning = "#B26A00",
                Error = "#C62828",
                Background = "#F5F7FA",
                Surface = "#FFFFFF",
                AppbarBackground = "#1B5FA8",
                AppbarText = "#FFFFFF",
                DrawerBackground = "#FFFFFF",
                DrawerText = "#1F2933",
                DrawerIcon = "#52606D",
                TextPrimary = "#1F2933",
                TextSecondary = "#52606D",
                Divider = "#D9E0E7",
                LinesDefault = "#D9E0E7",
            },
            PaletteDark = new PaletteDark
            {
                Primary = "#7FB2E5",
                PrimaryContrastText = "#0B1017",
                Secondary = "#4DD0E1",
                SecondaryContrastText = "#0B1017",
                Info = "#4FC3F7",
                Success = "#66BB6A",
                Warning = "#FFB74D",
                Error = "#EF5350",
                Background = "#12161C",
                Surface = "#1A1F27",
                AppbarBackground = "#1A1F27",
                AppbarText = "#E4E9F0",
                DrawerBackground = "#161B22",
                DrawerText = "#D7DDE5",
                DrawerIcon = "#9AA5B1",
                TextPrimary = "#E4E9F0",
                TextSecondary = "#9AA5B1",
                Divider = "#2B3441",
                LinesDefault = "#2B3441",
            },
            LayoutProperties = new LayoutProperties
            {
                DefaultBorderRadius = "8px",
                DrawerWidthLeft = "240px",
                AppbarHeight = "56px",
            },
        };
    }
}
