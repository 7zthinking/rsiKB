using System.Globalization;
using System.Text.RegularExpressions;

namespace KBEA.Core.Knowledge;

/// <summary>
/// 查询规则解析器（L0）：把自然语言里的硬分面抽出来，规则优先、模型兜底。
/// 目标是把"今年8月左右的、比之前简化了的、30页左右的ppt"这类描述转成结构化过滤条件。
/// </summary>
public sealed partial class QueryParser
{
    /// <summary>解析结果：硬过滤条件 + 剩余自由文本。</summary>
    public sealed class ParsedQuery
    {
        /// <summary>文件格式（如 pptx、pdf）。</summary>
        public string? Format { get; set; }

        /// <summary>时间窗起止（含）。</summary>
        public DateTimeOffset? TimeFrom { get; set; }

        /// <summary>时间窗结束（含）。</summary>
        public DateTimeOffset? TimeTo { get; set; }

        /// <summary>页数下界。</summary>
        public double? MinPages { get; set; }

        /// <summary>页数上界。</summary>
        public double? MaxPages { get; set; }

        /// <summary>命中的命名空间标签（如 version/simplified）。</summary>
        public List<string> Tags { get; } = [];

        /// <summary>解析后剩余的自由文本，用于稀疏检索。</summary>
        public string RemainingText { get; set; } = string.Empty;

        /// <summary>是否为纯结构化查询（无剩余文本）。</summary>
        public bool IsPureFacet => string.IsNullOrWhiteSpace(RemainingText);

        /// <summary>解析过程的提示说明，供 UI 展示"我理解成了什么"。</summary>
        public List<string> Notes { get; } = [];
    }

    private static readonly Dictionary<string, string> FormatAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ppt"] = "pptx", ["pptx"] = "pptx", ["幻灯片"] = "pptx", ["演示"] = "pptx", ["演示文稿"] = "pptx", ["slides"] = "pptx", ["slide"] = "pptx",
        ["doc"] = "docx", ["docx"] = "docx", ["word"] = "docx",
        ["xls"] = "xlsx", ["xlsx"] = "xlsx", ["excel"] = "xlsx", ["表格"] = "xlsx", ["sheet"] = "xlsx",
        ["pdf"] = "pdf",
        ["txt"] = "text", ["文本"] = "text",
        ["md"] = "markdown", ["markdown"] = "markdown",
        ["csv"] = "csv",
        ["图片"] = "image", ["照片"] = "image", ["png"] = "image", ["jpg"] = "image", ["jpeg"] = "image"
    };

    /// <summary>语义线索 → 命名空间标签。每条线索用独立正则，避免误命中其他维度。</summary>
    private static readonly (Regex Pattern, string Tag)[] TagHints =
    [
        (new Regex(@"简化|精简|删减|摘要版|概要版", RegexOptions.Compiled), "version/simplified"),
        (new Regex(@"旧版|历史|备份|归档|之前|老版本", RegexOptions.Compiled), "version/archive"),
        (new Regex(@"最终|终版|定稿|正式版|最新版|新的", RegexOptions.Compiled), "version/latest"),
        (new Regex(@"草稿|初稿|讨论稿|待定", RegexOptions.Compiled), "version/wip"),
        (new Regex(@"技术|架构|实现|接口|代码|偏技术", RegexOptions.Compiled), "audience/engineer"),
        (new Regex(@"汇报|报告|总结|简报", RegexOptions.Compiled), "purpose/report"),
        (new Regex(@"参考|范例|示例|模板|样板", RegexOptions.Compiled), "purpose/reference"),
        (new Regex(@"方案|设计|规划|设计稿", RegexOptions.Compiled), "purpose/plan"),
        (new Regex(@"会议|纪要|记录", RegexOptions.Compiled), "purpose/meeting"),
        (new Regex(@"需求|PRD", RegexOptions.Compiled), "purpose/requirement"),
        (new Regex(@"测试|用例", RegexOptions.Compiled), "purpose/test"),
        (new Regex(@"合同|协议|法规|合规|政策", RegexOptions.Compiled), "type/policy"),
        (new Regex(@"案例|复盘|经验|教训", RegexOptions.Compiled), "type/case"),
        (new Regex(@"教程|指南|手册|步骤|怎么做|如何", RegexOptions.Compiled), "type/howto"),
        (new Regex(@"原理|概念|术语|定义|公式", RegexOptions.Compiled), "type/concept"),
        (new Regex(@"最佳实践|设计模式|经验模式", RegexOptions.Compiled), "type/pattern"),
        (new Regex(@"战略|方向|路线图|roadmap", RegexOptions.Compiled), "type/direction")
    ];

    /// <summary>解析查询文本。</summary>
    public ParsedQuery Parse(string query)
    {
        var result = new ParsedQuery();
        if (string.IsNullOrWhiteSpace(query)) return result;

        var text = query.Trim();
        var remaining = text;

        // 1) 时间表达
        ExtractTime(text, result, ref remaining);

        // 2) 格式表达
        ExtractFormat(text, result, ref remaining);

        // 3) 页数表达
        ExtractPages(text, result, ref remaining);

        // 4) 语义标签线索：逐条独立正则匹配，只对命中的维度打标
        foreach (var (pattern, tag) in TagHints)
        {
            if (!pattern.IsMatch(remaining)) continue;
            if (result.Tags.Contains(tag)) continue;
            result.Tags.Add(tag);
            result.Notes.Add($"识别到线索「{tag.Split('/')[1]}」");
        }

        // 5) 清理剩余文本中的停用词与修饰词
        result.RemainingText = CleanRemaining(remaining);
        if (!string.IsNullOrWhiteSpace(result.RemainingText))
            result.Notes.Add($"关键词检索：{result.RemainingText}");
        return result;
    }

    /// <summary>时间表达：今年8月、去年、上个月、2026-08、8月、近三年。</summary>
    private static void ExtractTime(string text, ParsedQuery result, ref string remaining)
    {
        var now = DateTimeOffset.Now;
        int? relativeYear = null;

        // 1) 相对年份词：今年 / 去年 / 前年 / 明年
        var yearMatch = RelativeYearPattern().Match(remaining);
        if (yearMatch.Success)
        {
            relativeYear = yearMatch.Value switch
            {
                "去年" => now.Year - 1,
                "前年" => now.Year - 2,
                "明年" => now.Year + 1,
                _ => now.Year
            };
            remaining = RemoveMatch(remaining, RelativeYearPattern());
        }

        // 2) 绝对年月：2026-08 / 2026/08 / 2026年8月（优先级高于相对年份）
        var absolute = AbsoluteMonthPattern().Match(remaining);
        if (absolute.Success)
        {
            var year = int.Parse(absolute.Groups["y"].Value);
            var month = int.Parse(absolute.Groups["m"].Value);
            if (month is >= 1 and <= 12)
            {
                var loose = IsLoose(remaining);
                var pad = loose ? 1 : 0;
                result.TimeFrom = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-pad);
                result.TimeTo = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1 + pad).AddDays(-1);
                result.Notes.Add($"时间范围：{year} 年 {month} 月{(loose ? "（含前后各一个月）" : "")}");
                remaining = RemoveMatch(remaining, AbsoluteMonthPattern());
                return;
            }
        }

        // 3) 月份（含"今年8月"这类相对年份 + 月份的组合）
        var monthMatch = MonthPattern().Match(remaining);
        if (monthMatch.Success)
        {
            var month = int.Parse(monthMatch.Groups["m"].Value);
            var explicitYear = monthMatch.Groups["y"].Value;
            int year;
            if (!string.IsNullOrEmpty(explicitYear)) year = int.Parse(explicitYear);
            else if (relativeYear.HasValue) year = relativeYear.Value;
            else
            {
                // 缺年份：取"最近的过去该月"。若该月尚未到来（>= 当前月），取去年。
                var candidate = new DateTimeOffset(now.Year, month, 1, 0, 0, 0, TimeSpan.Zero);
                year = candidate > now ? now.Year - 1 : now.Year;
            }

            if (month is >= 1 and <= 12)
            {
                var loose = IsLoose(remaining);
                var pad = loose ? 1 : 0;
                result.TimeFrom = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-pad);
                result.TimeTo = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1 + pad).AddDays(-1);
                result.Notes.Add($"时间范围：{year} 年 {month} 月{(loose ? "（含前后各一个月）" : "")}");
                remaining = RemoveMatch(remaining, MonthPattern());
                return;
            }
        }

        // 4) 仅年份：今年 / 去年
        if (relativeYear.HasValue)
        {
            result.TimeFrom = new DateTimeOffset(relativeYear.Value, 1, 1, 0, 0, 0, TimeSpan.Zero);
            result.TimeTo = new DateTimeOffset(relativeYear.Value, 12, 31, 23, 59, 59, TimeSpan.Zero);
            result.Notes.Add($"时间范围：{relativeYear.Value} 年");
        }

        // 5) 近 N 个月 / 近 N 年
        var recent = RecentPattern().Match(remaining);
        if (recent.Success)
        {
            var amount = int.Parse(recent.Groups["n"].Value);
            var unit = recent.Groups["u"].Value;
            result.TimeFrom = unit == "年" ? now.AddYears(-amount) : now.AddMonths(-amount);
            result.TimeTo = now;
            result.Notes.Add($"时间范围：近 {amount} {unit}");
            remaining = RemoveMatch(remaining, RecentPattern());
        }
    }

    /// <summary>是否含"左右/前后/附近"等模糊修饰。</summary>
    private static bool IsLoose(string text) =>
        text.Contains("左右") || text.Contains("前后") || text.Contains("附近") ||
        text.Contains("大概") || text.Contains("大约");

    /// <summary>格式表达。</summary>
    private static void ExtractFormat(string text, ParsedQuery result, ref string remaining)
    {
        foreach (var (alias, format) in FormatAliases)
        {
            if (!text.Contains(alias, StringComparison.OrdinalIgnoreCase)) continue;
            result.Format = format;
            result.Notes.Add($"文件类型：{alias} → {format}");
            remaining = RemoveLiteral(remaining, alias);
            break;
        }
    }

    /// <summary>页数表达：30页、30 页左右、大概 30 页、超过 50 页。</summary>
    private static void ExtractPages(string text, ParsedQuery result, ref string remaining)
    {
        var match = PagesPattern().Match(text);
        if (!match.Success) return;
        var value = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        var tail = match.Groups["t"].Value;

        switch (tail)
        {
            case "左右" or "前后" or "附近":
                result.MinPages = value * 0.8;
                result.MaxPages = value * 1.25;
                break;
            case "以上" or "多于" or "超过" or "up":
                result.MinPages = value;
                break;
            case "以下" or "少于" or "不到" or "down":
                result.MaxPages = value;
                break;
            default:
                result.MinPages = value * 0.9;
                result.MaxPages = value * 1.1;
                break;
        }
        result.Notes.Add($"页数：{value}（{tail}）");
        remaining = RemoveMatch(remaining, PagesPattern());
    }

    /// <summary>清理剩余文本：去修饰词、去空白、保留有意义的词。</summary>
    private static string CleanRemaining(string text)
    {
        var result = text;
        foreach (var pattern in StopPatterns())
        {
            result = pattern.Replace(result, " ");
        }
        return string.Join(' ', result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string RemoveMatch(string text, Regex pattern) => pattern.Replace(text, " ");

    private static string RemoveLiteral(string text, string literal) =>
        text.Replace(literal, " ", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"今年|去年|前年|前年|明年")]
    private static partial Regex RelativeYearPattern();

    [GeneratedRegex(@"(?<y>\d{4})[年\-/\.](?<m>\d{1,2})(?![年\-/\.\d])")]
    private static partial Regex AbsoluteMonthPattern();

    [GeneratedRegex(@"(?:(?<y>\d{4})年)?(?<m>\d{1,2})月(?!\d)")]
    private static partial Regex MonthPattern();

    [GeneratedRegex(@"近(?<n>\d+)(?<u>个月|年|周)")]
    private static partial Regex RecentPattern();

    [GeneratedRegex(@"(?<n>\d+)\s*(?<t>页|张|slides?)(\s*(左右|前后|附近|以上|以下|多于|少于|超过|不到))?")]
    private static partial Regex PagesPattern();

    [GeneratedRegex(@"(简化|精简|删减|摘要版|概要版|旧版|历史|备份|归档|之前|老版本|最终|终版|定稿|正式版|最新版|新的|草稿|初稿|讨论稿|待定|技术|架构|实现|接口|代码|偏技术|汇报|报告|总结|简报|参考|范例|示例|模板|样板|方案|设计|规划|设计稿|会议|纪要|记录|需求|测试|用例|合同|协议|法规|合规|政策|案例|复盘|经验|教训|教程|指南|手册|步骤|怎么做|如何|原理|概念|术语|定义|公式|最佳实践|设计模式|经验模式|战略|方向|路线图)")]
    private static partial Regex TagRegex();

    private static IEnumerable<Regex> StopPatterns()
    {
        yield return new Regex(@"(的|了|吗|呢|吧|啊|呀|哦|嗯|请|帮我|找一下|找找|查找|一下|一个|一些|那些|这个|那个)", RegexOptions.Compiled);
        yield return new Regex(@"(左右|前后|附近|大概|大约|差不多|应该是|可能是)", RegexOptions.Compiled);
    }
}
