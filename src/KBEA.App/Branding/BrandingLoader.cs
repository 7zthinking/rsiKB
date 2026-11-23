using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KBEA.App.Branding;

/// <summary>
/// 品牌配置加载器：读程序目录 <c>branding/</c> 下的固化配置文件。
/// 约定文件名：<c>branding.yaml</c>（或 .yml） + <c>logo.png</c>。
/// 任何一步失败都回落到内置默认值，绝不阻断程序启动。
/// </summary>
public static class BrandingLoader
{
    /// <summary>品牌目录名（相对程序目录）。</summary>
    public const string DirectoryName = "branding";

    /// <summary>配置文件名（固定命名，发行方只改内容不改名）。</summary>
    public const string ConfigFileName = "branding.yaml";

    /// <summary>备用配置文件名（.yml 后缀）。</summary>
    public const string AlternateConfigFileName = "branding.yml";

    private static BrandingOptions? _cached;

    /// <summary>当前生效的品牌配置（首次读取后走缓存）。</summary>
    public static BrandingOptions Current => _cached ??= Load();

    /// <summary>品牌目录路径（程序目录下 branding/）。</summary>
    public static string BrandingDirectory => Path.Combine(AppContext.BaseDirectory, DirectoryName);

    /// <summary>配置文件路径（branding.yaml 优先，其次 branding.yml）。</summary>
    public static string? ConfigPath => FindConfigPath(BrandingDirectory);

    /// <summary>logo 图片路径（branding/ 下配置的 logo 文件名）。</summary>
    public static string? LogoPath => FindLogoPath(BrandingDirectory, Current);

    /// <summary>从磁盘读取品牌配置；文件缺失或解析失败时返回内置默认值。</summary>
    public static BrandingOptions Load() => Load(BrandingDirectory);

    /// <summary>从指定目录读取品牌配置（测试与自定义路径用）。</summary>
    public static BrandingOptions Load(string brandingDirectory)
    {
        var fallback = new BrandingOptions();
        var path = FindConfigPath(brandingDirectory);
        if (path is null) return fallback;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(HyphenatedNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var parsed = deserializer.Deserialize<BrandingOptions>(File.ReadAllText(path));
            return Normalize(parsed ?? fallback);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            // 配置文件坏了：用默认值继续跑，不让品牌配置问题挡住启动
            return fallback;
        }
    }

    /// <summary>在指定目录里定位配置文件（branding.yaml 优先，其次 branding.yml）。</summary>
    public static string? FindConfigPath(string brandingDirectory)
    {
        var yaml = Path.Combine(brandingDirectory, ConfigFileName);
        if (File.Exists(yaml)) return yaml;
        var yml = Path.Combine(brandingDirectory, AlternateConfigFileName);
        return File.Exists(yml) ? yml : null;
    }

    /// <summary>在指定目录里定位 logo 图片（按配置的文件名）。</summary>
    public static string? FindLogoPath(string brandingDirectory, BrandingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.LogoFileName)) return null;
        var path = Path.Combine(brandingDirectory, options.LogoFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>丢弃缓存，下次访问重新读盘。</summary>
    public static void Reload() => _cached = null;

    /// <summary>把解析结果收敛到合法值（空名回默认值、尺寸非法回默认）。</summary>
    private static BrandingOptions Normalize(BrandingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProductName)) options.ProductName = BrandingOptions.DefaultProductName;
        if (options.Edition is null) options.Edition = BrandingOptions.DefaultEdition;
        if (options.LogoFileName is null) options.LogoFileName = "logo.png";
        if (options.LogoSize <= 0) options.LogoSize = 30;
        return options;
    }
}
