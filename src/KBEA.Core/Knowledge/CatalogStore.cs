using System.Text.Json;
using System.Text.Json.Serialization;

namespace KBEA.Core.Knowledge;

/// <summary>
/// catalog 存储：读写 .7z/catalog/{hash}.json 与 .7z/manifest.json。
/// catalog 目录只放散装 JSON 文本，便于进 git 做文本级合并。
/// </summary>
public sealed class CatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _metadataPath;

    /// <summary>空间根目录，用于把旧版绝对路径迁移为相对路径。</summary>
    public string SpaceRoot { get; }

    public CatalogStore(string metadataPath, string? spaceRoot = null)
    {
        _metadataPath = Path.GetFullPath(metadataPath);
        CatalogDirectory = Path.Combine(_metadataPath, "catalog");
        DescribeDirectory = Path.Combine(_metadataPath, "describe");
        ManifestPath = Path.Combine(_metadataPath, "manifest.json");
        SpaceRoot = spaceRoot ?? Path.GetDirectoryName(_metadataPath.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
    }

    public string CatalogDirectory { get; }
    public string DescribeDirectory { get; }
    public string ManifestPath { get; }

    /// <summary>清理早期版本遗留的空目录（结构已收敛为 catalog/describe/meta.db/derived）。</summary>
    public void CleanupLegacyDirectories()
    {
        foreach (var name in new[] { "index", "lock", "assistant" })
        {
            var path = Path.Combine(_metadataPath, name);
            try
            {
                if (!Directory.Exists(path)) continue;
                if (Directory.EnumerateFileSystemEntries(path).Any()) continue;
                Directory.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>读取库级配置；不存在时返回默认配置。</summary>
    public KbManifest LoadManifest()
    {
        try
        {
            if (File.Exists(ManifestPath))
            {
                var manifest = JsonSerializer.Deserialize<KbManifest>(File.ReadAllText(ManifestPath), JsonOptions);
                if (manifest is not null) return manifest;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 配置损坏时回退默认值，不阻断扫描
        }
        return new KbManifest();
    }

    /// <summary>写入库级配置。</summary>
    public void SaveManifest(KbManifest manifest)
    {
        manifest.UpdatedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(_metadataPath);
        WriteAtomic(ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    /// <summary>读取单条 catalog 记录；兼容旧版格式（filePath / extendedProperties）。</summary>
    public CatalogEntry? Load(string hash)
    {
        var path = Path.Combine(CatalogDirectory, $"{hash}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<CatalogEntry>(File.ReadAllText(path), JsonOptions);
            if (entry is null) return null;
            NormalizeLegacy(entry, SpaceRoot);
            return entry;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 旧版 catalog JSON 用 filePath（绝对路径）、extension、fileSize、createdTime、modifiedTime、
    /// extendedProperties（字符串字典）。这里迁移为 paths[] 与 facts/properties 结构。
    /// </summary>
    public static void NormalizeLegacy(CatalogEntry entry, string? spaceRoot = null)
    {
        if (entry.Paths.Count == 0) return;

        // 旧格式存的是绝对路径，转成相对路径（相对空间根）
        for (var i = 0; i < entry.Paths.Count; i++)
        {
            var path = entry.Paths[i];
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (Path.IsPathRooted(path) && !string.IsNullOrEmpty(spaceRoot))
            {
                try
                {
                    var relative = Path.GetRelativePath(spaceRoot, path);
                    if (!relative.StartsWith("..", StringComparison.Ordinal))
                    {
                        entry.Paths[i] = relative.Replace('/', '\\');
                        continue;
                    }
                }
                catch (ArgumentException) { }
            }
            entry.Paths[i] = path.Replace('/', '\\');
        }
    }

    /// <summary>写入单条 catalog 记录（原子替换）。</summary>
    public void Save(CatalogEntry entry)
    {
        Directory.CreateDirectory(CatalogDirectory);
        entry.GeneratedAt = DateTimeOffset.Now;
        WriteAtomic(Path.Combine(CatalogDirectory, $"{entry.Hash}.json"),
            JsonSerializer.Serialize(entry, JsonOptions));
    }

    /// <summary>枚举全部 catalog 记录。</summary>
    public IReadOnlyList<CatalogEntry> LoadAll()
    {
        if (!Directory.Exists(CatalogDirectory)) return [];
        var result = new List<CatalogEntry>();
        foreach (var file in Directory.EnumerateFiles(CatalogDirectory, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CatalogEntry>(File.ReadAllText(file), JsonOptions);
                if (entry is null) continue;
                NormalizeLegacy(entry, SpaceRoot);
                result.Add(entry);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // 跳过损坏文件
            }
        }
        return result;
    }

    /// <summary>删除不再存在的文件对应的 catalog 记录，返回删除数量。</summary>
    public int RemoveStale(IReadOnlySet<string> liveHashes)
    {
        if (!Directory.Exists(CatalogDirectory)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(CatalogDirectory, "*.json"))
        {
            var hash = Path.GetFileNameWithoutExtension(file);
            if (liveHashes.Contains(hash)) continue;
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>删除孤儿描述文件（catalog 已无对应记录），返回删除数量。</summary>
    public int RemoveStaleDescribes(IReadOnlySet<string> liveHashes)
    {
        if (!Directory.Exists(DescribeDirectory)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(DescribeDirectory, "*.md"))
        {
            var hash = Path.GetFileNameWithoutExtension(file);
            if (liveHashes.Contains(hash)) continue;
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>写入 describe 文件；已存在时保留人工正文，仅刷新 frontmatter 与系统骨架段。</summary>
    public string WriteDescribe(CatalogEntry entry, string body)
    {
        Directory.CreateDirectory(DescribeDirectory);
        var path = Path.Combine(DescribeDirectory, $"{entry.Hash}.md");
        var existing = ReadDescribe(entry.Hash);

        // 已有文件：把新骨架写进 <!-- kb:auto --> 段，人工正文原样保留
        if (existing is not null && existing.Contains(AutoStartMarker, StringComparison.Ordinal))
        {
            var merged = MergeAutoSection(existing, body);
            WriteAtomic(path, RefreshFrontMatter(merged, entry));
            return path;
        }

        var frontMatter =
            $"""
            ---
            kb_id: {entry.Id}
            kb_format: {entry.Format}
            kb_source: {entry.PrimaryPath}
            kb_generated_at: {entry.GeneratedAt:O}
            kb_hash: {entry.Hash}
            ---

            """;
        WriteAtomic(path, frontMatter + body);
        return path;
    }

    /// <summary>刷新 frontmatter 中的系统字段（人工正文不受影响）。</summary>
    private static string RefreshFrontMatter(string markdown, CatalogEntry entry)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return markdown;
        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end <= 0) return markdown;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["kb_id"] = entry.Id,
            ["kb_format"] = entry.Format,
            ["kb_source"] = entry.PrimaryPath,
            ["kb_generated_at"] = entry.GeneratedAt.ToString("O"),
            ["kb_hash"] = entry.Hash
        };

        for (var i = 1; i < end; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;
            var key = lines[i][..separator].Trim();
            if (!fields.TryGetValue(key, out var value)) continue;
            lines[i] = $"{key}: {value}";
        }
        return string.Join('\n', lines);
    }

    /// <summary>系统骨架段的起止标记。</summary>
    public const string AutoStartMarker = "<!-- kb:auto -->";
    public const string AutoEndMarker = "<!-- /kb:auto -->";

    /// <summary>用新骨架替换旧骨架段，返回合并后的完整内容。</summary>
    private static string MergeAutoSection(string existing, string newBody)
    {
        var start = existing.IndexOf(AutoStartMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(AutoEndMarker, start, StringComparison.Ordinal);
        if (start < 0 || end < 0) return existing;

        // 新骨架的正文（去掉可能存在的外层标记）
        var newSection = newBody;
        var newStart = newSection.IndexOf(AutoStartMarker, StringComparison.Ordinal);
        if (newStart >= 0)
        {
            var newEnd = newSection.IndexOf(AutoEndMarker, newStart, StringComparison.Ordinal);
            newSection = newEnd > 0 ? newSection[newStart..(newEnd + AutoEndMarker.Length)] : newSection[newStart..];
        }
        else
        {
            newSection = $"{AutoStartMarker}\n\n{newBody.Trim()}\n\n{AutoEndMarker}";
        }

        var head = existing[..start];
        var tail = existing[(end + AutoEndMarker.Length)..];
        return head + newSection + tail;
    }

    /// <summary>读取 describe 文件内容。</summary>
    public string? ReadDescribe(string hash)
    {
        var path = Path.Combine(DescribeDirectory, $"{hash}.md");
        if (!File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// 提取 describe 中的人工正文（去掉 frontmatter 与系统骨架段）。
    /// 用于人工内容检索与 reconcile 报告。
    /// </summary>
    public static string ExtractManualBody(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
            if (end > 0) start = end + 1;
        }
        var body = string.Join('\n', lines.Skip(start)).Trim();

        var startMarker = body.IndexOf(AutoStartMarker, StringComparison.Ordinal);
        if (startMarker >= 0)
        {
            var endMarker = body.IndexOf(AutoEndMarker, startMarker, StringComparison.Ordinal);
            if (endMarker > 0)
            {
                var auto = body[startMarker..(endMarker + AutoEndMarker.Length)];
                body = body.Replace(auto, string.Empty).Trim();
            }
        }
        return body.Trim();
    }

    /// <summary>
    /// 原子写：先写临时文件再替换目标。
    /// 临时文件名带唯一后缀，避免并发写入（多任务同时处理同一文件）时互相抢占临时文件。
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var temporary = $"{path}.{Environment.ProcessId:x}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }
}
