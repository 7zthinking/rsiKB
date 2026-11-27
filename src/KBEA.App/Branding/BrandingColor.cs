namespace KBEA.App.Branding;

/// <summary>
/// 品牌颜色解析：把 branding.yaml 里写的颜色串规范成 WPF 颜色格式（#AARRGGBB）。
/// 不依赖 WPF 类型，便于单测。
/// </summary>
public static class BrandingColor
{
    /// <summary>
    /// 解析颜色串：支持 #RGB / #RRGGBB / #AARRGGBB，'#' 可省略。
    /// 解析不了返回 false（调用方回落到内置配色）。
    /// </summary>
    public static bool TryParse(string? text, out string color)
    {
        color = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (!value.StartsWith('#')) value = "#" + value;

        var digits = value[1..];
        // #RGB → #RRGGBB
        if (digits.Length == 3) digits = string.Concat(digits.Select(c => new string(c, 2)));
        // #RRGGBB → #AARRGGBB（补不透明 alpha）
        if (digits.Length == 6) digits = "FF" + digits;
        if (digits.Length != 8) return false;
        if (!digits.All(IsHexDigit)) return false;

        color = "#" + digits;
        return true;
    }

    /// <summary>取发行方指定的颜色；没指定或写坏了用内置配色。</summary>
    public static string Pick(string? configured, string fallback) =>
        TryParse(configured, out var color) ? color : fallback;

    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
