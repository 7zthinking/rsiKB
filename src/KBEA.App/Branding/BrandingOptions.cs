namespace KBEA.App.Branding;

/// <summary>
/// 品牌配置：程序目录 <c>branding/</c> 下的固化配置（logo + 文案）。
/// 发行方或二次开发者只需替换该目录里的文件即可换品牌，无需改代码。
/// 字段与 branding.yaml 一一对应（YAML 用 camelCase / kebab-case 均可，解析时忽略大小写与连字符）。
/// </summary>
public sealed class BrandingOptions
{
    /// <summary>品牌名（侧栏标题、窗口标题前缀、消息框标题）。</summary>
    public string ProductName { get; set; } = "知识库自进化助手";

    /// <summary>版本后缀（窗口标题里的「· 开源版」这类说明）。空则标题不带后缀。</summary>
    public string Edition { get; set; } = "开源版";

    /// <summary>logo 文件名（相对 branding 目录）。留空则用内置 logo。</summary>
    public string LogoFileName { get; set; } = "logo.png";

    /// <summary>一句话简介（设置页「关于我们」用）。空则用内置文案。</summary>
    public string Tagline { get; set; } = string.Empty;

    /// <summary>侧栏 logo 显示尺寸（像素）。0 表示用内置 30。</summary>
    public double LogoSize { get; set; } = 30;

    /// <summary>内置默认文案（配置缺失时兜底）。</summary>
    public const string DefaultProductName = "知识库自进化助手";

    /// <summary>内置默认后缀。</summary>
    public const string DefaultEdition = "开源版";

    /// <summary>窗口标题：品牌名 + 后缀（后缀为空时只有品牌名）。</summary>
    public string WindowTitle => string.IsNullOrWhiteSpace(Edition)
        ? ProductName
        : $"{ProductName} · {Edition}";
}
