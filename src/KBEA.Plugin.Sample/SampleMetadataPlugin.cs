using KBEA.Core.Plugins;

namespace KBEA.Plugin.Sample;

/// <summary>
/// 示例元数据插件：只认 .sampletxt 扩展名（避免干扰真实 .txt 的测试断言），
/// 基础层为每个文件产出 sample_marker 扩展属性与 sample 标签。
/// </summary>
public sealed class SampleMetadataPlugin : IMetadataPlugin
{
    public string PluginId => "sample.metadata";

    public string PluginName => "示例元数据插件";

    public string[] SupportedExtensions => [".sampletxt"];

    public MetadataLevel Level => MetadataLevel.Basic;

    public string[] ExtendedProperties => ["sample_marker"];

    public FileMetadata Scan(string filePath)
    {
        var info = new FileInfo(filePath);
        return new FileMetadata(
            filePath,
            info.Name,
            info.Extension,
            info.Exists ? info.Length : 0,
            info.Exists ? info.CreationTime : DateTime.MinValue,
            info.Exists ? info.LastWriteTime : DateTime.MinValue,
            ["sample"],
            new Dictionary<string, string> { ["sample_marker"] = "ok" });
    }
}
