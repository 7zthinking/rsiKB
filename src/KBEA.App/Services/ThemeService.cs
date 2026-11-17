using System.Windows;
using System.Windows.Media;

namespace KBEA.App.Services;

public static class ThemeService
{
    public static void Apply(string theme)
    {
        var dark = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        Set("WindowBackground", dark ? "#FF15191F" : "#FFF5F7FA");
        Set("PanelBackground", dark ? "#FF1C222B" : "#FFFFFFFF");
        Set("SidebarBackground", dark ? "#FF181D25" : "#FFF0F3F7");
        Set("CardBackground", dark ? "#FF232A35" : "#FFFFFFFF");
        Set("TextBrush", dark ? "#FFF1F4F8" : "#FF1D2733");
        Set("SecondaryTextBrush", dark ? "#FF9AA7B7" : "#FF667585");
        Set("BorderBrush", dark ? "#FF343E4C" : "#FFDDE3EA");
        Set("AccentBrush", dark ? "#FFFF9B55" : "#FFE87924");
        // 选中态要明显深于悬浮态（HoverBrush），避免被误认成鼠标悬浮高亮
        Set("AccentSoftBrush", dark ? "#FF7A4A22" : "#FFEFC49B");
        // 悬浮态要比面板底色明显深一档才看得出（此前浅色 #F0F4F9 在白底上几乎不可见）
        Set("HoverBrush", dark ? "#FF2B3542" : "#FFE2E9F2");
        Set("SuccessBrush", dark ? "#FF57C98B" : "#FF14804A");
        Set("WarningBrush", dark ? "#FFFFC766" : "#FFB54708");
    }

    private static void Set(string key, string color) => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
