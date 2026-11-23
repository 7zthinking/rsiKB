using System.Globalization;

namespace KBEA.Core.Knowledge;

/// <summary>
/// 规则标签引擎：入库即打，零模型成本。
/// 命名空间约定：维度/取值（如 format/pdf、temporal/2026-08、size/large）。
/// </summary>
public static class RuleTagger
{
    /// <summary>格式映射：扩展名 → 归一化格式标识。</summary>
    private static readonly Dictionary<string, string> FormatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "pdf",
        [".doc"] = "doc", [".docx"] = "docx",
        [".xls"] = "xls", [".xlsx"] = "xlsx", [".xlsm"] = "xlsm",
        [".ppt"] = "ppt", [".pptx"] = "pptx",
        [".md"] = "markdown", [".markdown"] = "markdown",
        [".txt"] = "text", [".log"] = "text", [".csv"] = "csv",
        [".json"] = "json", [".xml"] = "xml", [".yaml"] = "yaml", [".yml"] = "yaml",
        [".html"] = "html", [".htm"] = "html",
        [".rtf"] = "rtf", [".odt"] = "odt", [".ods"] = "ods", [".odp"] = "odp",
        [".png"] = "image", [".jpg"] = "image", [".jpeg"] = "image",
        [".gif"] = "image", [".bmp"] = "image", [".webp"] = "image", [".tif"] = "image", [".tiff"] = "image",
        [".mp4"] = "video", [".mov"] = "video", [".avi"] = "video",
        [".mp3"] = "audio", [".wav"] = "audio",
        [".zip"] = "archive", [".7z"] = "archive", [".rar"] = "archive",
        [".cs"] = "code", [".ts"] = "code", [".js"] = "code", [".py"] = "code",
        [".java"] = "code", [".c"] = "code", [".cpp"] = "code", [".h"] = "code",
        [".sql"] = "code", [".sh"] = "code", [".ps1"] = "code"
    };

    /// <summary>归一化扩展名 → 格式标识；未知扩展名回退为 "other"。</summary>
    public static string ResolveFormat(string extension) =>
        !string.IsNullOrWhiteSpace(extension) && FormatMap.TryGetValue(extension, out var format) ? format : "other";

    /// <summary>大小分段阈值（字节）。</summary>
    public static string SizeBucket(long size) => size switch
    {
        < 10 * 1024 => "tiny",
        < 1024 * 1024 => "small",
        < 10 * 1024 * 1024 => "medium",
        < 100 * 1024 * 1024 => "large",
        _ => "huge"
    };

    /// <summary>时间标签：精确到月（temporal/2026-08），便于"今年8月左右"的区间过滤。</summary>
    public static string TemporalTag(DateTimeOffset time) =>
        $"temporal/{time.Year:D4}-{time.Month:D2}";

    /// <summary>季度标签：temporal/2026-Q4。</summary>
    public static string QuarterTag(DateTimeOffset time) =>
        $"temporal/{time.Year:D4}-Q{(time.Month - 1) / 3 + 1}";

    /// <summary>位置标签：取相对路径的前两级目录，用 / 连接。</summary>
    public static IReadOnlyList<string> LocationTags(string relativePath)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(relativePath)) return result;
        var parts = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return result;
        var depth = Math.Min(2, parts.Length - 1);
        for (var i = 0; i < depth; i++)
        {
            var segment = parts[i].Trim();
            if (segment.Length == 0) continue;
            result.Add($"location/{segment}");
        }
        return result;
    }

    /// <summary>生成单文件的全部规则标签。</summary>
    public static IReadOnlyList<string> BuildTags(string relativePath, string extension, long size, DateTimeOffset modifiedTime, bool gitTracked)
    {
        var format = ResolveFormat(extension);
        var tags = new List<string>
        {
            $"format/{format}",
            $"size/{SizeBucket(size)}",
            TemporalTag(modifiedTime),
            QuarterTag(modifiedTime)
        };
        tags.AddRange(LocationTags(relativePath));
        tags.Add(gitTracked ? "git/tracked" : "git/untracked");

        // 页数分面只在 facts 已知时由 PageBucketFromFacts 打（真实页数）；
        // 此处不再用文件字节近似页数——曾导致同一文件同时带 huge/tiny 两个矛盾标签
        return tags.Distinct().ToArray();
    }

    /// <summary>基于 facts 中真实页数生成 pages 标签。</summary>
    public static string? PageBucketFromFacts(IReadOnlyDictionary<string, double> facts)
    {
        if (!facts.TryGetValue("page_count", out var pages) || pages <= 0) return null;
        return PageBucket((long)pages, null);
    }

    private static string? PageBucket(long? pageCount, string? format)
    {
        if (pageCount is null or <= 0) return null;
        return pageCount.Value switch
        {
            <= 10 => "tiny",
            <= 40 => "small",
            <= 120 => "medium",
            <= 300 => "large",
            _ => "huge"
        };
    }

    /// <summary>把标签按命名空间分组，供 UI 展示分面。</summary>
    public static IReadOnlyDictionary<string, List<string>> GroupByNamespace(IEnumerable<string> tags)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var separator = tag.IndexOf('/');
            if (separator <= 0) continue;
            var ns = tag[..separator];
            if (!result.TryGetValue(ns, out var list))
            {
                list = [];
                result[ns] = list;
            }
            list.Add(tag[(separator + 1)..]);
        }
        return result;
    }

    /// <summary>时间标签解析回 DateTimeOffset（月初）。用于把查询时间窗映射到标签。</summary>
    public static bool TryParseTemporal(string value, out DateTimeOffset time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)) return false;
        if (month is < 1 or > 12) return false;
        time = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        return true;
    }
}
