using System.Text.Json;

namespace KBEA.Core.Plugins;

/// <summary>
/// 插件启用来源：决定某个插件最终是否加载。
/// 优先级：用户显式设置 &gt; 发行方默认策略 &gt; 插件清单自带的 enabled。
/// </summary>
public enum PluginEnableSource
{
    /// <summary>插件清单自带的默认值（未做任何配置）。</summary>
    Default = 0,

    /// <summary>发行方策略：默认启用。</summary>
    PolicyEnabled,

    /// <summary>发行方策略：默认禁用。</summary>
    PolicyDisabled,

    /// <summary>用户显式启用。</summary>
    UserEnabled,

    /// <summary>用户显式禁用。</summary>
    UserDisabled
}

/// <summary>
/// 发行方插件默认启用策略：程序目录下的 <c>plugins.defaults.json</c>（固定命名）。
/// 发行方/二次开发者改这一个文件即可决定开箱默认启用哪些插件，用户仍可在设置里逐个调整。
/// </summary>
public sealed class PluginEnablePolicy
{
    /// <summary>策略文件名（相对程序目录，固定命名）。</summary>
    public const string FileName = "plugins.defaults.json";

    /// <summary>默认启用的插件 id 列表。</summary>
    public List<string> Enabled { get; set; } = [];

    /// <summary>默认禁用的插件 id 列表（优先于 <see cref="Enabled"/>）。</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>空策略（没有配置文件时的默认行为）。</summary>
    public static PluginEnablePolicy Empty { get; } = new();

    /// <summary>策略文件路径（程序目录下）。</summary>
    public static string PolicyPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>读取策略文件；文件缺失或解析失败时返回空策略（不改变现有行为）。</summary>
    public static PluginEnablePolicy Load(string? path = null)
    {
        path ??= PolicyPath;
        try
        {
            if (!File.Exists(path)) return Empty;
            var text = File.ReadAllText(path);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            var policy = JsonSerializer.Deserialize<PluginEnablePolicy>(
                text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return policy ?? Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 策略文件坏了：按「无策略」继续跑，不让发行配置问题挡住启动
            return Empty;
        }
    }

    /// <summary>取某插件在策略里的默认启用状态；null 表示策略未提及（沿用插件自带默认值）。</summary>
    public bool? GetDefaultEnabled(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return null;
        if (Disabled.Any(id => id.Equals(pluginId, StringComparison.OrdinalIgnoreCase))) return false;
        if (Enabled.Any(id => id.Equals(pluginId, StringComparison.OrdinalIgnoreCase))) return true;
        return null;
    }
}
