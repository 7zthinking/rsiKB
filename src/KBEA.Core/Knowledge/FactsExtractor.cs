using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace KBEA.Core.Knowledge;

/// <summary>
/// 数值分面提取器：零外部依赖，直接解析文件结构。
/// OOXML（docx/pptx/xlsx）用 System.IO.Compression 读内嵌 XML；PDF 解析 /Type /Page 计数。
/// </summary>
public static partial class FactsExtractor
{
    private const long MaxScanBytes = 8 * 1024 * 1024;

    /// <summary>提取单个文件的数值分面与扩展属性。</summary>
    public static (Dictionary<string, double> Facts, Dictionary<string, string> Properties) Extract(string filePath, string extension)
    {
        var facts = new Dictionary<string, double>();
        var properties = new Dictionary<string, string>();
        var format = RuleTagger.ResolveFormat(extension);

        try
        {
            var info = new FileInfo(filePath);
            switch (format)
            {
                case "docx":
                    ExtractDocx(filePath, facts);
                    break;
                case "pptx":
                    ExtractPptx(filePath, facts, properties);
                    break;
                case "xlsx":
                    ExtractXlsx(filePath, facts, properties);
                    break;
                case "pdf":
                    ExtractPdf(filePath, facts);
                    break;
                case "text":
                case "markdown":
                case "csv":
                    ExtractText(filePath, facts, properties);
                    break;
            }
            if (info.Length > 0) properties["byte_size"] = info.Length.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or ArgumentException)
        {
            // 单文件解析失败不影响整体扫描，facts 保持为空（未知不参与硬过滤）
            // 典型场景：扩展名是 .docx/.pptx 但内容不是合法 OOXML 包
        }
        catch (Exception)
        {
            // 兜底：任何未预期的解析异常都不应中断批量扫描
        }

        return (facts, properties);
    }

    /// <summary>docx：页数取 app.xml 的 Pages，词数取 Words。</summary>
    private static void ExtractDocx(string filePath, Dictionary<string, double> facts)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entry = archive.GetEntry("docProps/app.xml");
        if (entry is null) return;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var xml = reader.ReadToEnd();
        var pages = ReadElementValue(xml, "Pages");
        if (pages.HasValue) facts["page_count"] = pages.Value;
        var words = ReadElementValue(xml, "Words");
        if (words.HasValue) facts["word_count"] = words.Value;
        var paragraphs = ReadElementValue(xml, "Paragraphs");
        if (paragraphs.HasValue) facts["paragraph_count"] = paragraphs.Value;
        var lines = ReadElementValue(xml, "Lines");
        if (lines.HasValue) facts["line_count"] = lines.Value;
    }

    /// <summary>pptx：幻灯片数 = ppt/slides/slideN.xml 数量。</summary>
    private static void ExtractPptx(string filePath, Dictionary<string, double> facts, Dictionary<string, string> properties)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var slides = archive.Entries.Count(e =>
            e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains("slides/_rels", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains(".rels", StringComparison.OrdinalIgnoreCase));
        if (slides > 0) facts["slide_count"] = slides;
        facts["page_count"] = slides;

        var notes = archive.Entries.Count(e =>
            e.FullName.StartsWith("ppt/notesSlides/notesSlide", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        if (notes > 0) facts["notes_count"] = notes;

        var entry = archive.GetEntry("docProps/app.xml");
        if (entry is not null)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();
            var words = ReadElementValue(xml, "Words");
            if (words.HasValue) facts["word_count"] = words.Value;
            var title = ReadElementText(xml, "TitlesOfParts");
            if (!string.IsNullOrWhiteSpace(title)) properties["title_hint"] = title;
        }
    }

    /// <summary>xlsx：工作表数 = xl/worksheets/sheetN.xml 数量。</summary>
    private static void ExtractXlsx(string filePath, Dictionary<string, double> facts, Dictionary<string, string> properties)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var sheets = archive.Entries.Count(e =>
            e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains(".rels", StringComparison.OrdinalIgnoreCase));
        if (sheets > 0) facts["sheet_count"] = sheets;
        facts["page_count"] = sheets;

        var entry = archive.GetEntry("docProps/core.xml");
        if (entry is null) return;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var xml = reader.ReadToEnd();
        var title = ReadElementText(xml, "dc:title");
        if (!string.IsNullOrWhiteSpace(title)) properties["title"] = title;
        var creator = ReadElementText(xml, "dc:creator");
        if (!string.IsNullOrWhiteSpace(creator)) properties["author"] = creator;
    }

    /// <summary>PDF：统计 /Type /Page 对象数（不含 /Pages），大文件只读头部。</summary>
    private static void ExtractPdf(string filePath, Dictionary<string, double> facts)
    {
        var info = new FileInfo(filePath);
        long length = info.Length;
        var bufferSize = (int)Math.Min(length, MaxScanBytes);
        if (bufferSize <= 0) return;

        var buffer = new byte[bufferSize];
        using (var stream = File.OpenRead(filePath))
        {
            var read = stream.Read(buffer, 0, bufferSize);
            if (read <= 0) return;
            var text = Encoding.Latin1.GetString(buffer, 0, read);

            // 优先读取 /Count N（页树根节点的总页数，最可靠）
            var countMatches = Regex.Matches(text, @"/Count\s+(\d+)");
            var maxCount = 0;
            foreach (Match match in countMatches)
            {
                if (int.TryParse(match.Groups[1].Value, out var value) && value > maxCount) maxCount = value;
            }
            if (maxCount > 0)
            {
                facts["page_count"] = maxCount;
                return;
            }

            // 回退：统计 /Type /Page 出现次数（排除 /Pages）
            var pageMatches = Regex.Matches(text, @"/Type\s*/Page(?![s])");
            if (pageMatches.Count > 0) facts["page_count"] = pageMatches.Count;
        }
    }

    /// <summary>文本类：行数、字符数、词数。</summary>
    private static void ExtractText(string filePath, Dictionary<string, double> facts, Dictionary<string, string> properties)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        properties["encoding"] = reader.CurrentEncoding.WebName;

        long chars = 0, lines = 0, words = 0;
        var buffer = new char[8192];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            chars += read;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n') lines++;
                if (!char.IsWhiteSpace(buffer[i])) words++;
            }
        }
        facts["char_count"] = chars;
        facts["line_count"] = lines;
        facts["word_count"] = words;
    }

    /// <summary>从 XML 中读取元素文本值（忽略命名空间前缀差异）。</summary>
    private static double? ReadElementValue(string xml, string elementName)
    {
        var text = ReadElementText(xml, elementName);
        if (text is null) return null;
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    /// <summary>从 XML 中读取元素原始文本（用于标题、作者等字符串属性）。</summary>
    private static string? ReadElementText(string xml, string elementName)
    {
        var pattern = $"<(?:\\w+:)?{Regex.Escape(elementName)}[^>]*>([^<]*)</";
        var match = Regex.Match(xml, pattern);
        if (!match.Success) return null;
        var value = match.Groups[1].Value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// 从文件名/路径中识别版本线索：v2、修订版、最终版、终版、简化版、精简版、旧版、初稿等。
    /// 用于版本角色标注与"比之前简化了"的线索。
    /// </summary>
    public static string? DetectVersionMarker(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (var marker in SimplifiedMarkers) if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return "simplified";
        foreach (var marker in FinalMarkers) if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return "final";
        foreach (var marker in DraftMarkers) if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return "wip";
        foreach (var marker in ArchiveMarkers) if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return "archive";

        var version = VersionNumberPattern().Match(name);
        if (version.Success) return "versioned";
        return null;
    }

    private static readonly string[] SimplifiedMarkers = ["简化", "精简", "删减", "摘要版", "概要版", "简版"];
    private static readonly string[] FinalMarkers = ["最终", "终版", "定稿", "正式版", "final", "发布版"];
    private static readonly string[] DraftMarkers = ["草稿", "初稿", "讨论稿", "draft", "wip", "待定"];
    private static readonly string[] ArchiveMarkers = ["旧版", "历史", "备份", "归档", "old", "bak", "副本", "copy"];

    [GeneratedRegex(@"(?:^|[^a-zA-Z0-9])[vV](\d+)(?:\.(\d+))?(?![a-zA-Z0-9])")]
    private static partial Regex VersionNumberPattern();
}
