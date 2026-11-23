using System.Globalization;
using System.Security.Cryptography;
using KBEA.Core.Plugins;

namespace KBEA.Core.Knowledge;

/// <summary>
/// Ingest 管线：发现 → 哈希 → 规则标签 + facts → 聚类 → describe 骨架 → 查询投影。
/// 全部本地计算，无模型依赖；扫描结果写 catalog/{hash}.json（可进 git）。
/// P3 起实现随 kbea-plugin-wiki 分发（docs/plugin-architecture.md §5）；
/// 命名空间保持 KBEA.Core.Knowledge：契约与报告类型同源，宿主侧只需经 IWikiOperations 解析。
/// </summary>
public sealed class KnowledgeIngestService : IWikiOperations
{
    private readonly PluginManager _plugins = PluginManager.Instance;

    /// <summary>对整个空间执行一次整理（等价于只跑基础层）。</summary>
    public IngestReport Ingest(string spacePath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => IngestLayer(spacePath, MetadataJobKind.Basic, progress, null, null, cancellationToken);

    /// <inheritdoc />
    public IngestReport IngestLayer(
        string spacePath,
        MetadataJobKind kind,
        IProgress<string>? progress = null,
        IProgress<LayerProgress>? layerProgress = null,
        IProgress<PluginProgress>? pluginProgress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var root = Path.GetFullPath(spacePath);
        var metadataPath = Path.Combine(root, ".7z");
        var store = new CatalogStore(metadataPath, root);
        var manifest = store.LoadManifest();
        var report = new IngestReport { SpacePath = root, Layer = kind };

        if (!Directory.Exists(root)) return report;

        store.CleanupLegacyDirectories();

        // 旧记录可能缺失 id 导致键重复，这里按首次出现去重
        var existing = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in store.LoadAll())
        {
            var key = entry.Hash;
            if (string.IsNullOrWhiteSpace(key)) continue;
            existing.TryAdd(key, entry);
        }
        progress?.Report($"已载入 {existing.Count} 条既有索引");
        var liveHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<CatalogEntry>();

        // 基础层做全量扫描；上层只处理目录内已存在的文件（增量）
        var files = EnumerateFiles(root, manifest).ToList();
        var total = files.Count;
        layerProgress?.Report(new LayerProgress("扫描文件", 0, total));

        // 统计每个插件覆盖的文件数，用于面板展示各 plugin 的元数据构建情况
        var pluginHits = new Dictionary<string, (int Count, PluginProgress Info)>(StringComparer.Ordinal);
        var level = MetadataLevelMapping.ToLevel(kind);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = kind == MetadataJobKind.Basic
                    ? ProcessFile(file, root, manifest, existing, liveHashes)
                    : ProcessFileForLayer(file, root, manifest, existing, liveHashes, kind);
                if (entry is not null)
                {
                    entries.Add(entry);
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    var plugin = _plugins.GetPluginForExtension(extension);
                    if (plugin is not null && plugin.Level == level)
                    {
                        if (pluginHits.TryGetValue(plugin.PluginId, out var hit))
                            pluginHits[plugin.PluginId] = (hit.Count + 1, hit.Info);
                        else
                            pluginHits[plugin.PluginId] = (1, new PluginProgress
                            {
                                PluginId = plugin.PluginId,
                                PluginName = plugin.PluginName,
                                Level = plugin.Level,
                                Extensions = string.Join(" ", plugin.SupportedExtensions)
                            });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or NotSupportedException)
            {
                // 单文件失败不阻断整体整理
            }
            catch (Exception ex)
            {
                // 兜底：插件或解析器抛出未预期异常时跳过该文件，保证整理不中断
                progress?.Report($"跳过 {Path.GetFileName(file)}：{ex.GetType().Name}");
            }
            if (entries.Count % 50 == 0)
            {
                progress?.Report($"已处理 {entries.Count} 个文件…");
                layerProgress?.Report(new LayerProgress("扫描文件", entries.Count, total));
                ReportPluginProgress(pluginHits, pluginProgress, "扫描中", entries.Count, total);
            }
        }
        progress?.Report($"扫描完成：{entries.Count} 个文件");
        report.Scanned = entries.Count;
        layerProgress?.Report(new LayerProgress("扫描完成", total, total));
        ReportPluginProgress(pluginHits, pluginProgress, "扫描完成", total, total);

        // 版本聚类
        progress?.Report($"正在聚类版本谱系（{entries.Count} 个文件）…");
        var clusterer = new VersionClusterer();
        report.Clusters = clusterer.Cluster(entries, e => e.Properties.TryGetValue("content_fingerprint", out var v) ? v : null);
        progress?.Report($"版本簇完成：{report.Clusters} 个");

        // 写回 catalog + 生成 describe 骨架
        var written = 0;
        foreach (var entry in entries)
        {
            var previous = existing.TryGetValue(entry.Hash, out var prev) ? prev : null;
            var isNew = previous is null;
            var changed = previous is not null && HasChanged(previous, entry);

            if (isNew) report.Created++;
            else if (changed) report.Updated++;
            else report.Unchanged++;

            store.Save(entry);
            WriteDescribeSkeleton(store, entry, kind);
            report.Describes++;
            written++;
            if (written % 100 == 0)
            {
                progress?.Report($"已写入 {written}/{entries.Count} 条索引…");
                layerProgress?.Report(new LayerProgress("写入索引", written, entries.Count));
            }
        }
        progress?.Report($"索引写入完成：{written} 条");

        // 清理已删除文件的记录
        report.Removed = store.RemoveStale(liveHashes);

        // 清理孤儿描述文件（catalog 已无对应记录）
        report.RemovedDescribes = store.RemoveStaleDescribes(liveHashes);

        // 重建查询投影
        layerProgress?.Report(new LayerProgress("重建检索索引", 0, 1));
        var search = new KnowledgeSearchService(metadataPath);
        search.RebuildIndex();
        layerProgress?.Report(new LayerProgress("重建检索索引", 1, 1));

        manifest.LastIngestAt = DateTimeOffset.Now;
        store.SaveManifest(manifest);

        report.Elapsed = DateTimeOffset.Now - started;
        progress?.Report(report.Summary);
        return report;
    }

    /// <summary>处理单个文件：哈希 → 规则标签 + facts → 合并已有记录。</summary>
    private CatalogEntry? ProcessFile(string filePath, string root, KbManifest manifest,
        Dictionary<string, CatalogEntry> existing, HashSet<string> liveHashes,
        IProgress<PluginProgress>? pluginProgress = null)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) return null;

        var extension = info.Extension.ToLowerInvariant();
        if (manifest.ExcludeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return null;

        var hash = ComputeSha256(filePath);
        liveHashes.Add(hash);

        var relative = Path.GetRelativePath(root, filePath).Replace('/', '\\');
        var format = RuleTagger.ResolveFormat(extension);

        // facts：内置提取器 + 插件扩展属性（按扩展名分发到全部匹配插件，逐个合并）
        var (facts, properties) = FactsExtractor.Extract(filePath, extension);
        foreach (var plugin in _plugins.GetPluginsForExtension(extension))
        {
            try
            {
                var metadata = plugin.Scan(filePath);
                foreach (var (key, value) in metadata.ExtendedProperties)
                {
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        facts[key] = number;
                    else
                        properties[key] = value;
                }
                foreach (var tag in metadata.Tags) facts.TryAdd($"_tag_{tag}", 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or NotSupportedException)
            {
                // 单个插件失败不阻断其他插件，更不阻断整体整理
            }
        }

        var gitTracked = existing.TryGetValue(hash, out var previous) &&
                         previous.Tags.Contains("git/tracked", StringComparer.OrdinalIgnoreCase);

        var tags = RuleTagger.BuildTags(relative, extension, info.Length, new DateTimeOffset(info.LastWriteTimeUtc), gitTracked);
        var pageBucket = RuleTagger.PageBucketFromFacts(facts);
        if (pageBucket is not null && !tags.Contains($"pages/{pageBucket}")) tags = tags.Append($"pages/{pageBucket}").ToArray();

        var entry = new CatalogEntry
        {
            Id = $"sha256:{hash}",
            Paths = [relative],
            Format = format,
            Size = info.Length,
            Mtime = new DateTimeOffset(info.LastWriteTimeUtc),
            Tags = tags.ToList(),
            Facts = facts,
            Properties = properties,
            DescribeRef = $"describe/{hash}.md"
        };

        // 合并同内容的多路径（副本/软链接）
        if (existing.TryGetValue(hash, out var old))
        {
            if (!old.Paths.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                entry.Paths = old.Paths.Append(relative).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            entry.Entities = old.Entities;
            entry.Concepts = old.Concepts;
            entry.Related = old.Related;
            entry.ClusterId = old.ClusterId;
        }

        properties["content_fingerprint"] = hash;
        return entry;
    }

    /// <summary>
    /// 上层（类型层/专题层）处理：基于基础层已有的 catalog 记录，叠加该层插件的产出。
    /// 基础层没跑过的文件会被跳过——上层依赖基础层的身份与标签。
    /// </summary>
    private CatalogEntry? ProcessFileForLayer(string filePath, string root, KbManifest manifest,
        Dictionary<string, CatalogEntry> existing, HashSet<string> liveHashes, MetadataJobKind kind)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) return null;

        var extension = info.Extension.ToLowerInvariant();
        if (manifest.ExcludeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return null;

        var hash = ComputeSha256(filePath);
        liveHashes.Add(hash);

        // 基础层尚未产出该文件的记录，跳过（上层不负责建身份）
        if (!existing.TryGetValue(hash, out var baseEntry)) return null;

        var level = MetadataLevelMapping.ToLevel(kind);
        var plugins = _plugins.GetPluginsForExtension(extension)
            .Where(p => p.Level == level && p.Enabled)
            .ToList();
        if (plugins.Count == 0) return null;

        var relative = Path.GetRelativePath(root, filePath).Replace('/', '\\');
        var entry = new CatalogEntry
        {
            Id = baseEntry.Id,
            Paths = baseEntry.Paths.Count > 0 ? [.. baseEntry.Paths] : [relative],
            Format = baseEntry.Format,
            Size = baseEntry.Size,
            Mtime = baseEntry.Mtime,
            Tags = [.. baseEntry.Tags],
            Facts = new Dictionary<string, double>(baseEntry.Facts),
            Properties = new Dictionary<string, string>(baseEntry.Properties),
            Entities = [.. baseEntry.Entities],
            Concepts = [.. baseEntry.Concepts],
            Related = [.. baseEntry.Related],
            ClusterId = baseEntry.ClusterId,
            DescribeRef = baseEntry.DescribeRef,
            GeneratedAt = baseEntry.GeneratedAt,
            Generator = baseEntry.Generator,
            PluginState = baseEntry.PluginState
        };

        var contributors = new List<string>();
        foreach (var plugin in plugins)
        {
            try
            {
                var metadata = plugin.Scan(filePath);
                foreach (var (key, value) in metadata.ExtendedProperties)
                {
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        entry.Facts[key] = number;
                    else
                        entry.Properties[key] = value;
                }
                foreach (var tag in metadata.Tags)
                {
                    var namespaced = $"{level.ToString().ToLowerInvariant()}/{tag}";
                    if (!entry.Tags.Contains(namespaced, StringComparer.OrdinalIgnoreCase))
                        entry.Tags.Add(namespaced);
                }

                // 类型层插件可追加结构化摘要行，写入 describe 的 highlights 段
                if (plugin is ITypeMetadataPlugin typePlugin)
                {
                    var highlights = typePlugin.DescribeHighlights(filePath, metadata);
                    if (highlights.Count > 0)
                    {
                        var joined = string.Join(" | ", highlights);
                        entry.Properties["type_highlights"] = entry.Properties.TryGetValue("type_highlights", out var prior)
                            ? $"{prior} | {joined}" : joined;
                    }
                }

                contributors.Add(plugin.PluginId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or NotSupportedException)
            {
                // 单个插件失败不阻断其他插件
            }
        }

        // 有插件实际产出时登记生成时间与生成器（多插件合并记录全部贡献者）
        if (contributors.Count > 0)
        {
            entry.Properties[$"metadata_{level.ToString().ToLowerInvariant()}_at"] = DateTimeOffset.Now.ToString("O");
            entry.Generator = contributors.Count == 1
                ? $"{level.ToString().ToLowerInvariant()}+plugin:{contributors[0]}"
                : $"{level.ToString().ToLowerInvariant()}+plugins:{string.Join(",", contributors)}";
        }

        return entry;
    }

    /// <summary>把各插件的覆盖情况上报给调用方（面板按 plugin 展示构建情况）。</summary>
    private static void ReportPluginProgress(
        Dictionary<string, (int Count, PluginProgress Info)> pluginHits,
        IProgress<PluginProgress>? pluginProgress,
        string stage,
        int processed,
        int total)
    {
        if (pluginProgress is null) return;
        foreach (var (_, hit) in pluginHits)
        {
            pluginProgress.Report(new PluginProgress
            {
                PluginId = hit.Info.PluginId,
                PluginName = hit.Info.PluginName,
                Level = hit.Info.Level,
                Stage = stage,
                Processed = hit.Count,
                Total = total,
                Extensions = hit.Info.Extensions,
                UpdatedAt = DateTimeOffset.Now
            });
        }
    }

    /// <summary>判断记录是否有实质变化（用于增量统计）。</summary>
    private static bool HasChanged(CatalogEntry previous, CatalogEntry current)
    {
        if (previous.Size != current.Size) return true;
        if (Math.Abs((previous.Mtime - current.Mtime).TotalSeconds) > 1) return true;
        if (previous.Tags.Count != current.Tags.Count) return true;
        if (previous.Facts.Count != current.Facts.Count) return true;
        if (!previous.Paths.SequenceEqual(current.Paths, StringComparer.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>生成 describe 机械骨架（零 LLM）：facts 表 + 标题结构 + 原始引用。</summary>
    private static void WriteDescribeSkeleton(CatalogStore store, CatalogEntry entry, MetadataJobKind kind)
    {
        var name = Path.GetFileName(entry.PrimaryPath);
        var stem = Path.GetFileNameWithoutExtension(name);
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(CatalogStore.AutoStartMarker);
        builder.AppendLine();
        builder.AppendLine($"# {stem}");
        builder.AppendLine();
        builder.AppendLine($"- 文件名：{name}");
        builder.AppendLine($"- 类型：{entry.Format}");
        builder.AppendLine($"- 大小：{entry.Size} 字节");
        builder.AppendLine($"- 修改时间：{entry.Mtime:yyyy-MM-dd HH:mm}");

        if (entry.Facts.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 结构化事实");
            builder.AppendLine();
            builder.AppendLine("| 项 | 值 |");
            builder.AppendLine("|---|---|");
            foreach (var (key, value) in entry.Facts.OrderBy(f => f.Key))
                builder.AppendLine($"| {key} | {value:0.##} |");
        }

        // 类型层产出的结构化摘要
        if (entry.Properties.TryGetValue("type_highlights", out var highlights) && !string.IsNullOrWhiteSpace(highlights))
        {
            builder.AppendLine();
            builder.AppendLine("## 类型要点");
            builder.AppendLine();
            foreach (var line in highlights.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                builder.AppendLine($"- {line}");
        }

        var tags = entry.Tags.Where(t => !t.StartsWith("size/", StringComparison.Ordinal)).ToArray();
        if (tags.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 标签");
            builder.AppendLine();
            builder.AppendLine(string.Join(' ', tags.Select(t => $"`{t}`")));
        }

        builder.AppendLine();
        builder.AppendLine("## 原始引用");
        builder.AppendLine();
        builder.AppendLine($"> 来源: `{entry.PrimaryPath}`");
        builder.AppendLine();
        builder.AppendLine("## 元数据生成记录");
        builder.AppendLine();
        builder.AppendLine($"- 最近层级：{MetadataLevelMapping.DisplayName(kind)}");
        builder.AppendLine($"- 生成器：{entry.Generator}");
        builder.AppendLine($"- 生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine(CatalogStore.AutoEndMarker);
        store.WriteDescribe(entry, builder.ToString());
    }

    /// <summary>枚举空间文件，按 manifest 排除规则过滤。</summary>
    private static IEnumerable<string> EnumerateFiles(string root, KbManifest manifest)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var excludes = manifest.ExcludeDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (excludes.Contains(name)) continue;
                    if (name.StartsWith('.')) continue;
                    pending.Push(entry);
                }
                else yield return entry;
            }
        }
    }

    /// <summary>计算文件 SHA-256。</summary>
    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
