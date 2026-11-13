using System.Text;

namespace KBEA.Core.Plugins;

/// <summary>纯文本 / Markdown / 日志 / 轻量结构化文本 的基础元数据插件。</summary>
public sealed class TextMetadataPlugin : IMetadataPlugin
{
    public string PluginId => "builtin.text";
    public string PluginName => "文本文件元数据";
    public string[] SupportedExtensions { get; } = { ".txt", ".md", ".log", ".csv", ".json" };
    public string[] ExtendedProperties { get; } = { "word_count", "line_count", "char_count", "encoding" };

    public FileMetadata Scan(string filePath)
    {
        var info = new FileInfo(filePath);
        var (wordCount, lineCount, charCount, encodingName) = AnalyzeContent(filePath);

        var extended = new Dictionary<string, string>
        {
            ["word_count"] = wordCount.ToString(),
            ["line_count"] = lineCount.ToString(),
            ["char_count"] = charCount.ToString(),
            ["encoding"] = encodingName
        };

        return new FileMetadata(
            FilePath: Path.GetFullPath(filePath),
            FileName: info.Name,
            Extension: info.Extension,
            FileSize: info.Length,
            CreatedTime: info.CreationTime,
            ModifiedTime: info.LastWriteTime,
            Tags: [],
            ExtendedProperties: extended);
    }

    private static (int words, int lines, int chars, string encodingName) AnalyzeContent(string filePath)
    {
        try
        {
            // 读取前若干 KB 做编码检测 + 行数统计，避免超大文件全量加载
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);

            var lineCount = 0;
            var charCount = 0;
            var wordCount = 0;
            var buffer = new char[4096];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                charCount += read;
                // 统计换行
                for (int i = 0; i < read; i++)
                    if (buffer[i] == '\n') lineCount++;
                // 统计单词：简单按空白分词
                var segment = new Span<char>(buffer, 0, read);
                wordCount += CountWords(segment);
            }
            // 非空且不以换行结尾时，最后一行未计入
            if (charCount > 0 && !EndsWithNewline(filePath)) lineCount++;

            var encodingName = reader.CurrentEncoding.WebName;
            return (wordCount, lineCount, charCount, encodingName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0, 0, "unknown");
        }
    }

    private static int CountWords(ReadOnlySpan<char> span)
    {
        int count = 0;
        bool inWord = false;
        foreach (var c in span)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }

    private static bool EndsWithNewline(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length == 0) return false;
            stream.Seek(-1, SeekOrigin.End);
            var b = stream.ReadByte();
            return b == '\n' || b == '\r';
        }
        catch { return false; }
    }
}
