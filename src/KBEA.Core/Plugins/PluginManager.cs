namespace KBEA.Core.Plugins;

/// <summary>IMetadataPlugin 的轻量注册表。内置插件通过静态列表注册，后续可扩展到程序集扫描。</summary>
public sealed class PluginManager
{
    public static PluginManager Instance { get; } = new();

    private readonly List<IMetadataPlugin> _plugins;

    private PluginManager()
    {
        _plugins = new List<IMetadataPlugin>();
        RegisterBuiltIn();
    }

    public IReadOnlyList<IMetadataPlugin> Plugins => _plugins;

    /// <summary>按扩展名查找第一个匹配的插件；未注册扩展名返回 null。</summary>
    public IMetadataPlugin? GetPluginForExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        var normalized = ext.StartsWith('.') ? ext : "." + ext;
        normalized = normalized.ToLowerInvariant();
        return _plugins.FirstOrDefault(p => p.SupportedExtensions
            .Any(e => e.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
    }

    public void Register(IMetadataPlugin plugin)
    {
        if (plugin is null) throw new ArgumentNullException(nameof(plugin));
        if (_plugins.Any(p => p.PluginId == plugin.PluginId)) return;
        _plugins.Add(plugin);
    }

    /// <summary>注册 KBEA.Core 内置插件。</summary>
    private void RegisterBuiltIn()
    {
        // TODO: 未来改为通过程序集扫描 IMetadataPlugin 实现自动发现
        Register(new TextMetadataPlugin());
        Register(new OfficeMetadataPlugin());
    }

    /// <summary>取指定层级下所有启用的插件。</summary>
    public IReadOnlyList<IMetadataPlugin> GetByLevel(MetadataLevel level) =>
        _plugins.Where(p => p.Level == level && p.Enabled).ToList();
}
