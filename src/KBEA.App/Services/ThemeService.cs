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
        Set("AccentBrush", dark ? "#FF67A6FF" : "#FF2563EB");
        Set("AccentSoftBrush", dark ? "#FF203B5A" : "#FFE7F0FF");
        Set("HoverBrush", dark ? "#FF2B3542" : "#FFF0F4F9");
        Set("SuccessBrush", dark ? "#FF57C98B" : "#FF14804A");
        Set("WarningBrush", dark ? "#FFFFC766" : "#FFB54708");
    }

    private static void Set(string key, string color) => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
