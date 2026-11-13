using System.Text.Json;

namespace KBEA.Core.Plugins;

public interface IAssistantProvider { string DisplayName { get; } }
public interface IReportProvider { string DisplayName { get; } }
public interface IProjectMgmtProvider { string DisplayName { get; } }
public interface IIndexEngine { Task RebuildAsync(string mountPath, CancellationToken cancellationToken = default); }
public interface ICatalogRuleSource { string DisplayName { get; } }
public interface IInfoCardProvider { string DisplayName { get; } }

/// <summary>元数据层级：决定插件产出的元数据深度与执行顺序。</summary>
public enum MetadataLevel
{
    /// <summary>基础层：通用规则标签与事实提取，对所有文件生效。</summary>
    Basic = 0,

    /// <summary>类型层：按文件格式专用解析（如 PPT 大纲、PDF 目录、Word 标题）。</summary>
    Type = 1,

    /// <summary>专题层：按业务专题聚合的语义元数据（摘要、语义标签、关联），依赖模型。</summary>
    Semantic = 2
}

/// <summary>文件元数据扫描插件。针对特定扩展名集合，可产出扩展属性（如 word_count、page_count）。</summary>
public interface IMetadataPlugin
{
    string PluginId { get; }
    string PluginName { get; }
    string[] SupportedExtensions { get; }
    FileMetadata Scan(string filePath);
    string[] ExtendedProperties { get; } // e.g. ["word_count", "page_count"]

    /// <summary>该插件产出的元数据层级，默认基础层。</summary>
    MetadataLevel Level => MetadataLevel.Basic;

    /// <summary>该插件是否启用，可被用户按库关闭。</summary>
    bool Enabled => true;
}

/// <summary>类型层插件：除扫描外，还可产出按格式定制的结构化摘要（如幻灯片标题列表）。</summary>
public interface ITypeMetadataPlugin : IMetadataPlugin
{
    /// <summary>产出面向该类型的结构化摘要行，用于写入 describe 骨架。</summary>
    IReadOnlyList<string> DescribeHighlights(string filePath, FileMetadata metadata);
}

/// <summary>单个文件的元数据记录。扩展属性键值对由插件定义。</summary>
public record FileMetadata(
    string FilePath,
    string FileName,
    string Extension,
    long FileSize,
    DateTime CreatedTime,
    DateTime ModifiedTime,
    string[] Tags,
    IReadOnlyDictionary<string, string> ExtendedProperties);

public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string EntryAssembly { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public sealed class PluginCatalog
{
    public IReadOnlyList<PluginManifest> Discover(string? pluginRoot = null)
    {
        pluginRoot ??= Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginRoot)) return [];
        var result = new List<PluginManifest>();
        foreach (var file in Directory.EnumerateFiles(pluginRoot, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id)) result.Add(manifest);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken optional plugin must not prevent the open-source core from starting.
            }
        }
        return result;
    }
}
