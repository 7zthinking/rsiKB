using System.Windows;
using System.Windows.Media;
using KBEA.App.Branding;

namespace KBEA.App.Services;

public static class ThemeService
{
    /// <summary>主题切换后触发（图标等在加载时固化颜色的元素据此刷新）。</summary>
    public static event Action? ThemeChanged;

    /// <summary>内置配色（branding 未指定时的兜底）。</summary>
    private static readonly (string Light, string Dark) Accent = ("#FFE87924", "#FFFF9B55");
    private static readonly (string Light, string Dark) AccentSoft = ("#FFEFC49B", "#FF7A4A22");
    private static readonly (string Light, string Dark) Success = ("#FF14804A", "#FF57C98B");
    private static readonly (string Light, string Dark) Warning = ("#FFB54708", "#FFFFC766");

    public static void Apply(string theme)
    {
        var dark = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        var branding = BrandingLoader.Current;

        Set("WindowBackground", dark ? "#FF15191F" : "#FFF5F7FA");
        Set("PanelBackground", dark ? "#FF1C222B" : "#FFFFFFFF");
        Set("SidebarBackground", dark ? "#FF181D25" : "#FFF0F3F7");
        Set("CardBackground", dark ? "#FF232A35" : "#FFFFFFFF");
        Set("TextBrush", dark ? "#FFF1F4F8" : "#FF1D2733");
        Set("SecondaryTextBrush", dark ? "#FF9AA7B7" : "#FF667585");
        Set("BorderBrush", dark ? "#FF343E4C" : "#FFDDE3EA");
        // 强调色系：branding 指定则用发行方的，亮/暗主题共用同一色
        Set("AccentBrush", Pick(branding.Accent, dark ? Accent.Dark : Accent.Light));
        // 选中态要明显深于悬浮态（HoverBrush），避免被误认成鼠标悬浮高亮
        Set("AccentSoftBrush", Pick(branding.AccentSoft, dark ? AccentSoft.Dark : AccentSoft.Light));
        // 悬浮态要比面板底色明显深一档才看得出（此前浅色 #F0F4F9 在白底上几乎不可见）
        Set("HoverBrush", dark ? "#FF2B3542" : "#FFE2E9F2");
        Set("SuccessBrush", Pick(branding.Success, dark ? Success.Dark : Success.Light));
        Set("WarningBrush", Pick(branding.Warning, dark ? Warning.Dark : Warning.Light));
        ThemeChanged?.Invoke();
    }

    /// <summary>取发行方指定的颜色；没指定或写坏了用内置配色。</summary>
    private static string Pick(string? configured, string fallback) => BrandingColor.Pick(configured, fallback);

    private static void Set(string key, string color) => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
