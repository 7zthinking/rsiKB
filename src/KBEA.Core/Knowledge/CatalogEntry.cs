using System.Text.Json.Serialization;

namespace KBEA.Core.Knowledge;

/// <summary>
/// catalog/{hash}.json 的单文件记录：以内容 SHA-256 为身份，与路径解耦。
/// 该文件是机器索引，进 git；人工描述另存 describe/{hash}.md。
/// </summary>
public sealed class CatalogEntry
{
    /// <summary>内容身份，形如 "sha256:ABC123..."。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>该内容出现过的所有相对路径（副本、软链接、重命名后的新路径都会追加）。</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>归一化格式标识，如 pdf / pptx / docx / markdown。</summary>
    public string Format { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset Mtime { get; set; }

    /// <summary>命名空间标签，如 format/pdf、temporal/2026-08、version/simplified。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>数值分面，如 page_count=30、word_count=8200。</summary>
    public Dictionary<string, double> Facts { get; set; } = [];

    /// <summary>非数值扩展属性，如 encoding。</summary>
    public Dictionary<string, string> Properties { get; set; } = [];

    public List<string> Entities { get; set; } = [];

    public List<string> Concepts { get; set; } = [];

    /// <summary>关联文档 hash（同版本簇成员或内容相近文件）。</summary>
    public List<string> Related { get; set; } = [];

    /// <summary>所属版本簇 id。</summary>
    public string? ClusterId { get; set; }

    /// <summary>指向 describe/{hash}.md。</summary>
    public string? DescribeRef { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>生成器标识，如 rule+v0.1。</summary>
    public string Generator { get; set; } = "rule+v0.1";

    public PluginStateEntry PluginState { get; set; } = new();

    /// <summary>文件内容哈希（不含前缀），与 catalog 文件名一致。旧版记录无 id 时用文件名兜底。</summary>
    [JsonIgnore]
    public string Hash
    {
        get
        {
            if (Id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return Id[7..];
            if (!string.IsNullOrWhiteSpace(Id)) return Id;
            // 旧版 catalog 记录没有 id 字段，用主路径的文件名作为唯一键
            if (!string.IsNullOrWhiteSpace(PrimaryPath)) return Path.GetFileName(PrimaryPath);
            return string.Empty;
        }
    }

    /// <summary>主路径：优先第一条，其次按字典序取首个。</summary>
    [JsonIgnore]
    public string PrimaryPath => Paths.Count == 0 ? string.Empty : Paths[0];

    /// <summary>旧版 catalog JSON 的 filePath 字段（写入时映射为 paths[0]，读取时并入 paths）。</summary>
    [JsonPropertyName("filePath")]
    public string? LegacyFilePath
    {
        get => null;
        set { if (!string.IsNullOrWhiteSpace(value) && !Paths.Contains(value)) Paths.Add(value); }
    }
}

/// <summary>插件管线各步骤的执行状态，用于断点续传。</summary>
public sealed class PluginStateEntry
{
    public string? Parser { get; set; }
    public string? Chunker { get; set; }
    public string? Embedder { get; set; }
    public string? Grapher { get; set; }
}

/// <summary>.7z/manifest.json：库级配置，随空间走并进 git。</summary>
public sealed class KbManifest
{
    /// <summary>元数据 schema 版本，程序按此做增量迁移。</summary>
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>扫描排除的目录名（相对路径片段匹配）。</summary>
    public List<string> ExcludeDirectories { get; set; } =
    [
        ".git", ".7z", "bin", "obj", "node_modules", ".vs", ".idea", ".dotnet",
        "packages", "dist", "build", ".next", ".venv", "__pycache__",
        "artifacts", "publish", "output", ".trae", ".vscode", ".claude"
    ];

    /// <summary>扫描排除的扩展名。</summary>
    public List<string> ExcludeExtensions { get; set; } = [".tmp", ".temp", ".bak", ".log~"];

    /// <summary>标签词表版本号，升级词表时递增。</summary>
    public int TagVocabularyVersion { get; set; } = 1;

    /// <summary>上次整理时间。</summary>
    public DateTimeOffset? LastIngestAt { get; set; }
}
