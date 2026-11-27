using System.Reflection;

namespace KBEA.Core.Versioning;

/// <summary>
/// 应用版本：<b>单一真相源</b>。
/// 版本号只在 <c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c> 里维护一次，
/// 由 MSBuild 注入为程序集属性 <c>AssemblyInformationalVersionAttribute</c>；
/// 这里读取该属性，供 UI 显示、插件兼容判断与检查更新使用。
/// 任何地方都不要再硬编码版本号字符串。
/// </summary>
public static class AppVersion
{
    /// <summary>版本号（如 "0.4.0"）。来自程序集的 informational version。</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>带 v 前缀的显示形式（如 "v0.4.0"）。</summary>
    public static string Display => "v" + Version;

    /// <summary>
    /// 读取程序集版本：优先 AssemblyInformationalVersion（去掉 +commit 后缀），
    /// 回退 AssemblyName.Version。
    /// </summary>
    private static string ReadVersion()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // MSBuild 默认带 "+<commit sha>" 后缀，去掉
            var plus = informational.IndexOf('+');
            var core = plus >= 0 ? informational[..plus] : informational;
            if (!string.IsNullOrWhiteSpace(core)) return core.Trim();
        }
        var version = assembly.GetName().Version;
        if (version is not null)
            return $"{version.Major}.{version.Minor}.{version.Build}";
        return "0.0.0";
    }
}
