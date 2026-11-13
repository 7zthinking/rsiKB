using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KBEA.Core.Knowledge;

/// <summary>
/// 版本簇：把"同类型多版本"的文件归到一簇，并标注每个成员的版本角色。
/// 信号由弱到强：文件名归一化 → 内容 SimHash → git 血缘。
/// </summary>
public sealed class VersionClusterer
{
    /// <summary>归一化文件名时剥离的噪声词。长词在前，避免"简化版"被"简化"先吃掉后残留"版"。</summary>
    private static readonly string[] NoiseTokens =
    [
        "最新版", "最新版本", "新版本", "最终版", "最终版本", "旧版本", "历史版本",
        "简单版", "概要版", "摘要版", "简化版", "精简版", "缩减版", "删减版",
        "最终", "终版", "定稿", "正式", "新版", "旧版", "历史",
        "备份", "归档", "副本", "复制", "修改", "修订", "更新",
        "copy", "final", "draft", "temp", "tmp", "bak", "backup",
        "草稿", "初稿", "讨论稿", "简化", "精简", "简版", "删减", "摘要", "概要"
    ];

    /// <summary>文件名前缀中剥离的版本标记，如 v2、修订版2、_final。边界不能吞掉中文。</summary>
    private static readonly Regex VersionNoisePattern = new(
        @"(?i)(?:v|ver|version)\s*(\d{1,3})(?:\.\d+)*|(?:^|[^a-z0-9一-鿿])(\d{1,3})(?:\.\d+)*(?![a-z0-9一-鿿])|修订版?|终版?|最终版?|定稿|最新版?|新版本?|旧版本?|final|draft|copy|副本|\(\d+\)|（\d+）",
        RegexOptions.Compiled);

    /// <summary>扩展名 → 文档族，用于限定同簇必须是同类文件。</summary>
    private static readonly Dictionary<string, string> DocumentFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "pdf", ["doc"] = "word", ["docx"] = "word", ["rtf"] = "word", ["odt"] = "word",
        ["ppt"] = "slides", ["pptx"] = "slides", ["odp"] = "slides",
        ["xls"] = "sheet", ["xlsx"] = "sheet", ["xlsm"] = "sheet", ["ods"] = "sheet",
        ["md"] = "text", ["markdown"] = "text", ["txt"] = "text", ["csv"] = "text"
    };

    /// <summary>对一批 catalog 记录做聚类，写回 cluster_id / related / version/* 标签。</summary>
    public int Cluster(IReadOnlyList<CatalogEntry> entries, Func<CatalogEntry, string?> contentHashProvider)
    {
        if (entries.Count == 0) return 0;

        // 第一步：按"归一化文件名 + 文档族"分组
        var groups = new Dictionary<string, List<CatalogEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Paths.Count == 0) continue;
            var name = Path.GetFileName(entry.PrimaryPath);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var key = BuildGroupKey(name, entry.Format);
            if (key.Length == 0) continue;
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(entry);
        }

        var clusterCount = 0;
        foreach (var group in groups.Values)
        {
            if (group.Count < 2) continue;
            var clusterId = BuildClusterId(group);
            var ordered = group.OrderBy(e => e.Mtime).ToList();
            var center = ComputeCenterHash(ordered, contentHashProvider);

            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                entry.ClusterId = clusterId;
                var role = DetermineRole(entry, ordered, i, center);
                entry.Tags.RemoveAll(t => t.StartsWith("version/", StringComparison.Ordinal));
                entry.Tags.Add($"version/{role}");

                var siblings = ordered.Where((_, index) => index != i).Select(e => e.Hash).ToList();
                entry.Related = siblings;
                entry.Related.AddRange(entry.Related.Where(r => !siblings.Contains(r)));
                entry.Related = entry.Related.Distinct().ToList();
            }
            clusterCount++;
        }

        // 未成簇的文件标记为 latest/single
        foreach (var entry in entries.Where(e => string.IsNullOrEmpty(e.ClusterId)))
        {
            if (entry.Tags.Any(t => t.StartsWith("version/", StringComparison.Ordinal))) continue;
            entry.Tags.Add("version/latest");
        }

        return clusterCount;
    }

    /// <summary>生成归一化分组键：归一文件名 + 文档族。</summary>
    public static string BuildGroupKey(string fileName, string format)
    {
        var family = DocumentFamily.TryGetValue(format, out var f) ? f : format;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var normalized = NormalizeName(stem);
        if (normalized.Length == 0) return string.Empty;
        return $"{family}::{normalized}";
    }

    /// <summary>文件名归一化：剥离版本噪声、数字后缀、空白与标点。</summary>
    public static string NormalizeName(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return string.Empty;
        var text = stem;
        // 长词优先剥离（NoiseTokens 已按长度降序排列）
        foreach (var token in NoiseTokens)
        {
            text = text.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        // 剥离版本数字与标记
        text = VersionNoisePattern.Replace(text, string.Empty);
        // 剥离日期串 20260810 / 2026-08-10 / 2026_08_10
        text = Regex.Replace(text, @"\d{4}[-_]?\d{2}[-_]?\d{2}", string.Empty);
        // 剥离版本噪声词剥离后残留的孤立修饰字
        text = text.Replace("版", string.Empty);
        // 只保留字母数字与中文
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c > 127) builder.Append(c);
        }
        return builder.ToString().Trim().ToLowerInvariant();
    }

    /// <summary>计算簇 id：取最早成员的归一化名 + 最早日期月，保证同簇稳定。</summary>
    private static string BuildClusterId(List<CatalogEntry> group)
    {
        var earliest = group.OrderBy(e => e.Mtime).First();
        var name = NormalizeName(Path.GetFileNameWithoutExtension(earliest.PrimaryPath));
        if (name.Length == 0) name = "cluster";
        if (name.Length > 32) name = name[..32];
        var stamp = earliest.Mtime.ToString("yyyyMM", CultureInfo.InvariantCulture);
        return $"c-{stamp}-{name}";
    }

    /// <summary>簇内容指纹中心（SimHash 多数投票）。</summary>
    private static ulong ComputeCenterHash(List<CatalogEntry> ordered, Func<CatalogEntry, string?> contentHashProvider)
    {
        if (ordered.Count == 0) return 0;
        if (ordered.Count == 1) return SimHash.FromText(ordered[0].PrimaryPath);

        var hashes = new List<ulong>();
        foreach (var entry in ordered)
        {
            var content = contentHashProvider(entry);
            hashes.Add(string.IsNullOrEmpty(content)
                ? SimHash.FromText(entry.PrimaryPath)
                : SimHash.FromText(content));
        }
        var accumulator = new int[64];
        foreach (var hash in hashes)
        {
            for (var bit = 0; bit < 64; bit++)
            {
                var set = (hash >> bit) & 1;
                accumulator[bit] += set == 1 ? 1 : -1;
            }
        }
        ulong center = 0;
        for (var bit = 0; bit < 64; bit++)
        {
            if (accumulator[bit] > 0) center |= 1UL << bit;
        }
        return center;
    }

    /// <summary>判定版本角色：simplified / archive / latest / final / wip / full。</summary>
    private static string DetermineRole(CatalogEntry entry, List<CatalogEntry> ordered, int index, ulong center)
    {
        var marker = FactsExtractor.DetectVersionMarker(entry.PrimaryPath);
        if (marker == "simplified") return "simplified";
        if (marker == "wip") return "wip";
        if (marker == "archive") return "archive";

        var centerPages = MedianPages(ordered);
        var entryPages = entry.Facts.TryGetValue("page_count", out var pages) ? pages : 0;
        var maxPages = MaxPages(ordered);

        // 显著少于簇中位数 → 简化版
        if (centerPages > 0 && entryPages > 0 && entryPages < centerPages * 0.75) return "simplified";
        // 显著多于簇中位数 → 完整版
        if (centerPages > 0 && entryPages > 0 && maxPages > 0 && entryPages > maxPages * 0.9 && entryPages >= centerPages * 1.25) return "full";

        if (marker == "final") return "final";

        // 明确带版本号但不是最新的（v2/v3 等）→ 历史版
        if (marker == "versioned" && index != ordered.Count - 1) return "archive";

        // 无版本词：簇内页数最多者为完整版，最新者为 latest，其余为 archive
        if (maxPages > 0 && entryPages >= maxPages) return "full";
        if (index == ordered.Count - 1) return "latest";
        return "archive";
    }

    private static double MaxPages(List<CatalogEntry> entries)
    {
        var values = entries
            .Select(e => e.Facts.TryGetValue("page_count", out var v) ? v : 0)
            .Where(v => v > 0)
            .ToList();
        return values.Count == 0 ? 0 : values.Max();
    }

    private static double MedianPages(List<CatalogEntry> entries)
    {
        var values = entries
            .Select(e => e.Facts.TryGetValue("page_count", out var v) ? v : 0)
            .Where(v => v > 0)
            .OrderBy(v => v)
            .ToList();
        if (values.Count == 0) return 0;
        return values[values.Count / 2];
    }
}

/// <summary>SimHash：64 位内容指纹，用于近重复文件聚类。</summary>
public static class SimHash
{
    public static ulong FromText(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return 0;

        var accumulator = new int[64];
        foreach (var (token, weight) in tokens)
        {
            var hash = Fnv1a64(token);
            for (var bit = 0; bit < 64; bit++)
            {
                var set = (hash >> bit) & 1;
                accumulator[bit] += set == 1 ? weight : -weight;
            }
        }

        ulong result = 0;
        for (var bit = 0; bit < 64; bit++)
        {
            if (accumulator[bit] > 0) result |= 1UL << bit;
        }
        return result;
    }

    /// <summary>汉明距离相似度（0~1）。</summary>
    public static double Similarity(ulong a, ulong b)
    {
        if (a == 0 || b == 0) return 0;
        var xor = a ^ b;
        var distance = 0;
        while (xor != 0)
        {
            distance += (int)(xor & 1);
            xor >>= 1;
        }
        return 1.0 - distance / 64.0;
    }

    private static List<(string Token, int Weight)> Tokenize(string text)
    {
        var counts = new Dictionary<string, int>();
        var buffer = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c > 127)
            {
                buffer.Append(c);
            }
            else if (buffer.Length > 0)
            {
                var token = buffer.ToString();
                counts[token] = counts.GetValueOrDefault(token) + 1;
                buffer.Clear();
            }
        }
        if (buffer.Length > 0)
        {
            var token = buffer.ToString();
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }
        return counts.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static ulong Fnv1a64(string text)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }
}
