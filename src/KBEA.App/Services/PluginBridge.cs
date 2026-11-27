using KBEA.Core.Plugins;

namespace KBEA.App.Services;

/// <summary>
/// App 级静态桥：把 PluginHostService 的 P4 扩展点暴露给不便注入的服务路径
/// （DialogService 打开 SettingsWindow、MountTabViewModel 预览/详情、PreviewHelper 检索预览）。
/// MainViewModel 创建宿主后赋值；退出置 null。
/// </summary>
public static class PluginBridge
{
    public static PluginHostService? Instance { get; set; }

    /// <summary>预览链第一棒：插件按扩展名接手（未命中返回 null）。</summary>
    public static (byte[]? ImageData, string Text)? TryPluginPreview(string filePath) =>
        Instance?.TryPluginPreview(filePath);

    /// <summary>详情面板插件动态字段。</summary>
    public static IReadOnlyList<(string Name, string Value)> GetPluginFields(string filePath) =>
        Instance?.GetPluginFields(filePath) ?? [];

    /// <summary>插件设置页集合（SettingsWindow 打开时用）。</summary>
    public static IReadOnlyList<SettingsPageVm>? GetSettingsPages() => Instance?.GetSettingsPages();

    /// <summary>插件清单与加载状态（P5：设置窗口「插件管理」页）。</summary>
    public static IReadOnlyList<PluginStatusVm> GetPluginStatuses() => Instance?.GetPluginStatuses() ?? [];

    /// <summary>插件「关于我们」卡片集合（SettingsWindow 打开时用）。</summary>
    public static IReadOnlyList<AboutCardInfo> GetAboutCards() => Instance?.GetAboutCards() ?? [];
}
