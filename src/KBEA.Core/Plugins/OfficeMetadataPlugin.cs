using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace KBEA.Core.Plugins;

/// <summary>
/// 类型层插件：按 Office 文档格式做专用解析，产出结构化要点。
/// 与基础层的"页数/字数"统计不同，这里抽取可读内容线索（幻灯片标题、表格标题、文档标题）。
/// </summary>
public sealed class OfficeMetadataPlugin : ITypeMetadataPlugin
{
    public string PluginId => "builtin.office.type";
    public string PluginName => "Office 文档类型解析";
    public string[] SupportedExtensions { get; } = { ".pptx", ".docx", ".xlsx" };
    public string[] ExtendedProperties { get; } = { "type_title", "type_section_count" };
    public MetadataLevel Level => MetadataLevel.Type;

    public FileMetadata Scan(string filePath)
    {
        var info = new FileInfo(filePath);
        var extension = info.Extension.ToLowerInvariant();
        var title = string.Empty;
        var sections = 0;

        try
        {
            switch (extension)
            {
                case ".pptx":
                    (title, sections) = ReadPresentation(filePath);
                    break;
                case ".docx":
                    (title, sections) = ReadDocument(filePath);
                    break;
                case ".xlsx":
                    (title, sections) = ReadWorkbook(filePath);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or ArgumentException)
        {
            // 解析失败保持空值，不阻断
        }

        var extended = new Dictionary<string, string>
        {
            ["type_title"] = title,
            ["type_section_count"] = sections.ToString()
        };

        return new FileMetadata(
            FilePath: Path.GetFullPath(filePath),
            FileName: info.Name,
            Extension: info.Extension,
            FileSize: info.Length,
            CreatedTime: info.CreationTime,
            ModifiedTime: info.LastWriteTime,
            Tags: [extension.TrimStart('.')],
            ExtendedProperties: extended);
    }

    public IReadOnlyList<string> DescribeHighlights(string filePath, FileMetadata metadata)
    {
        var highlights = new List<string>();
        var title = metadata.ExtendedProperties.GetValueOrDefault("type_title");
        if (!string.IsNullOrWhiteSpace(title)) highlights.Add($"标题：{title}");

        var count = metadata.ExtendedProperties.GetValueOrDefault("type_section_count");
        if (int.TryParse(count, out var sections) && sections > 0)
            highlights.Add($"章节/幻灯片数：{sections}");

        // PPT 额外抽取前几张幻灯片的标题，形成可读大纲
        if (filePath.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var slideTitle in ReadSlideTitles(filePath, 8))
                    highlights.Add($"幻灯片：{slideTitle}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or NotSupportedException)
            {
                // 忽略
            }
        }

        return highlights;
    }

    /// <summary>pptx：读第一张幻灯片的标题占位文本 + 统计幻灯片数。</summary>
    private static (string Title, int Sections) ReadPresentation(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var slides = archive.Entries.Count(e =>
            e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains(".rels", StringComparison.OrdinalIgnoreCase));
        var title = ReadFirstSlideTitle(archive);
        return (title, slides);
    }

    private static string ReadFirstSlideTitle(ZipArchive archive)
    {
        var entry = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                        !e.FullName.Contains(".rels", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (entry is null) return string.Empty;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return ExtractFirstTextRun(reader.ReadToEnd());
    }

    /// <summary>按顺序抽取各张幻灯片的首个文本串，用于生成大纲。</summary>
    private static IEnumerable<string> ReadSlideTitles(string filePath, int limit)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entries = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                        !e.FullName.Contains(".rels", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SlideNumber(e.FullName))
            .Take(limit);

        foreach (var entry in entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = ExtractFirstTextRun(reader.ReadToEnd());
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    private static int SlideNumber(string path)
    {
        var match = Regex.Match(path, @"slide(\d+)\.xml$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : int.MaxValue;
    }

    /// <summary>docx：读 core.xml 标题 + 统计段落数。</summary>
    private static (string Title, int Sections) ReadDocument(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var title = string.Empty;
        var core = archive.GetEntry("docProps/core.xml");
        if (core is not null)
        {
            using var stream = core.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            title = ExtractTagText(reader.ReadToEnd(), "dc:title");
        }

        var sections = 0;
        var document = archive.GetEntry("word/document.xml");
        if (document is not null)
        {
            using var stream = document.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();
            sections = Regex.Matches(xml, @"<w:p[ >]").Count;
        }
        return (title, sections);
    }

    /// <summary>xlsx：读 core.xml 标题 + 统计工作表数。</summary>
    private static (string Title, int Sections) ReadWorkbook(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var sheets = archive.Entries.Count(e =>
            e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains(".rels", StringComparison.OrdinalIgnoreCase));
        var title = string.Empty;
        var core = archive.GetEntry("docProps/core.xml");
        if (core is not null)
        {
            using var stream = core.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            title = ExtractTagText(reader.ReadToEnd(), "dc:title");
        }
        return (title, sheets);
    }

    /// <summary>取第一个 a:t 文本节点的内容。</summary>
    private static string ExtractFirstTextRun(string xml)
    {
        var match = Regex.Match(xml, @"<a:t>([^<]*)</a:t>");
        if (!match.Success) return string.Empty;
        return match.Groups[1].Value.Trim();
    }

    /// <summary>取指定标签（带命名空间前缀）的文本内容。</summary>
    private static string ExtractTagText(string xml, string tag)
    {
        var pattern = $"<(?:\\w+:)?{Regex.Escape(tag)}[^>]*>([^<]*)</";
        var match = Regex.Match(xml, pattern);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }
}
