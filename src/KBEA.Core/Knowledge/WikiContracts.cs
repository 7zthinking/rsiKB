using KBEA.Core.Plugins;

namespace KBEA.Core.Knowledge;

/// <summary>
/// 知识 WIKI（元数据整理）能力契约（P3 插件化，docs/plugin-architecture.md §5）：
/// 接口与报告类型留在 Core，Ingest 引擎实现搬进 kbea-plugin-wiki 插件。
/// Worker 的元数据任务循环（MetadataScheduler）通过 <see cref="WikiOperationsProvider"/> 解析实现；
/// 插件未加载时返回 null，任务标记失败并提示安装 wiki 插件。
/// 检索（KnowledgeSearchService/QueryParser）是核心体验，留在 Core 不迁。
/// </summary>
public interface IWikiOperations
{
    /// <summary>对指定空间执行指定层级的元数据整理（基础层全量扫描；类型/专题层增量叠加）。</summary>
    IngestReport IngestLayer(
        string spacePath,
        MetadataJobKind kind,
        IProgress<string>? progress = null,
        IProgress<LayerProgress>? layerProgress = null,
        IProgress<PluginProgress>? pluginProgress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// WIKI 实现注册点：插件 Init 时调用 Register 注入实现，Worker 调度器经 Instance 取用。
/// 进程内单实现；重复 Register 以最后一次为准（重启加载，不会热切换）。
/// </summary>
public static class WikiOperationsProvider
{
    public static IWikiOperations? Instance { get; private set; }

    public static void Register(IWikiOperations implementation) => Instance = implementation;
}

/// <summary>整理结果统计（原 KnowledgeIngestService.IngestReport，P3 随契约提升为 Core 类型）。</summary>
public sealed class IngestReport
{
    public int Scanned { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Removed { get; set; }
    public int RemovedDescribes { get; set; }
    public int Clusters { get; set; }
    public int Describes { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string SpacePath { get; set; } = string.Empty;

    /// <summary>本次执行对应的层级。</summary>
    public MetadataJobKind Layer { get; set; } = MetadataJobKind.Basic;

    public string Summary =>
        $"扫描 {Scanned} 个文件 · 新建 {Created} · 更新 {Updated} · 未变 {Unchanged} · 清理 {Removed} · 版本簇 {Clusters} · 描述 {Describes}";
}

/// <summary>分层执行的细粒度进度。</summary>
public readonly record struct LayerProgress(string Stage, int Processed, int Total);
