using KBEA.Core.Knowledge;

namespace KBEA.Core.Plugins;

/// <summary>
/// IMetadataWorkbench 默认实现：无状态，按传入的空间根目录定位 .7z 元数据，
/// 与 KnowledgeIngestService 走同一套 CatalogStore/RuleTagger/检索投影。
/// </summary>
public sealed class MetadataWorkbench : IMetadataWorkbench
{
    /// <summary>由空间根目录定位元数据目录（约定 .7z）。</summary>
    private static CatalogStore OpenStore(string spacePath)
    {
        var root = Path.GetFullPath(spacePath);
        return new CatalogStore(Path.Combine(root, ".7z"), root);
    }

    public CatalogEntry? LoadEntry(string spacePath, string hash)
    {
        try { return OpenStore(spacePath).Load(hash); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void SaveEntry(string spacePath, CatalogEntry entry) => OpenStore(spacePath).Save(entry);

    /// <summary>把插件要点作为独立小节追加到 describe 文件末尾（人工正文与系统骨架都不动）。失败静默。</summary>
    public void AppendDescribeHighlights(string spacePath, string hash, IReadOnlyList<string> highlights)
    {
        if (highlights.Count == 0) return;
        try
        {
            var store = OpenStore(spacePath);
            var existing = store.ReadDescribe(hash);
            if (existing is null) return;
            var updated = existing.TrimEnd() + $"\n\n## 插件要点\n\n- {string.Join(" | ", highlights)}\n";
            Directory.CreateDirectory(store.DescribeDirectory);
            File.WriteAllText(Path.Combine(store.DescribeDirectory, $"{hash}.md"), updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // describe 追加失败不影响宿主
        }
    }

    public IReadOnlyList<string> BuildRuleTags(string relativePath, long size, DateTimeOffset mtime, bool gitTracked) =>
        RuleTagger.BuildTags(relativePath, Path.GetExtension(relativePath), size, mtime, gitTracked);

    /// <summary>请求重建 FTS 检索投影。best-effort：检索库被占用等原因导致失败时留待下次整理重建。</summary>
    public void RequestReindex(string spacePath)
    {
        try
        {
            new KnowledgeSearchService(Path.Combine(Path.GetFullPath(spacePath), ".7z")).RebuildIndex();
        }
        catch (Exception)
        {
            // 重建失败不抛出：插件不应因宿主工具面的瞬时故障而中断
        }
    }
}
