using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KBEA.Core.Team;

/// <summary>
/// 空间元数据导出：把共享空间的文件条目导出为 NDJSON（相对路径/哈希/标签/描述/大小/mtime）。
/// 只导出元数据，不带走文件本体；未声明共享的空间根本不出机器。
/// 增量导出：size+mtime 未变化的文件直接复用上一次的哈希。
/// </summary>
public sealed class MetadataExporter
{
    /// <summary>超过此大小不计算内容哈希（用 big:size:mtime 标记），避免导出耗时失控。</summary>
    public const long HashSizeLimit = 64 * 1024 * 1024;

    /// <summary>
    /// 导出一个空间的元数据到 outDir（&lt;spaceId&gt;.ndjson），返回条目数。
    /// </summary>
    public int Export(Guid spaceId, string spaceName, string spacePath, string outDir, string teamId = "")
    {
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, $"{spaceId:N}.ndjson");
        var cachePath = Path.Combine(outDir, "export-cache.json");
        var cache = LoadCache(cachePath);
        var sb = new StringBuilder();
        var count = 0;
        var newCache = new Dictionary<string, CacheEntry>();

        foreach (var relative in EnumerateFiles(spacePath))
        {
            var fullPath = Path.Combine(spacePath, relative);
            var info = new FileInfo(fullPath);
            var mtimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            var key = relative.ToLowerInvariant();
            if (cache.TryGetValue(key, out var cached) && cached.Size == info.Length && cached.MtimeMs == mtimeMs)
            {
                newCache[key] = cached;
                var hash = cached.Hash;
                AppendEntry(sb, spaceId, spaceName, relative, hash, info.Length, mtimeMs, teamId);
                count++;
                continue;
            }

            var computed = ComputeHash(fullPath, info.Length, mtimeMs);
            newCache[key] = new CacheEntry(info.Length, mtimeMs, computed);
            AppendEntry(sb, spaceId, spaceName, relative, computed, info.Length, mtimeMs, teamId);
            count++;
        }

        WriteAtomic(target, sb.ToString());
        TeamStore.WriteJson(cachePath, newCache.ToDictionary(pair => pair.Key, pair => new[]
            { pair.Value.Size.ToString(), pair.Value.MtimeMs.ToString(), pair.Value.Hash }));
        return count;
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void AppendEntry(StringBuilder sb, Guid spaceId, string spaceName, string relative,
        string hash, long size, long mtimeMs, string teamId)
    {
        var entry = new MetadataEntry
        {
            TeamId = teamId,
            SpaceId = spaceId,
            SpaceName = spaceName,
            Path = relative,
            Hash = hash,
            Size = size,
            Mtime = mtimeMs / 1000,
            Tags = [],
            Description = string.Empty
        };
        sb.Append(JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false }));
        sb.Append('\n');
    }

    private static string ComputeHash(string fullPath, long size, long mtimeMs)
    {
        if (size > HashSizeLimit) return $"big:{size}:{mtimeMs}";
        try
        {
            using var stream = File.OpenRead(fullPath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return $"err:{size}:{mtimeMs}";
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var files = new List<string>();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(directory);
                    if (name is ".git" or ".7z") continue;
                    if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(directory);
                }
                foreach (var file in Directory.EnumerateFiles(current))
                    files.Add(Path.GetRelativePath(root, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var file in files) yield return file;
        }
    }

    private static Dictionary<string, CacheEntry> LoadCache(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
            var result = new Dictionary<string, CacheEntry>();
            if (raw is null) return result;
            foreach (var (key, value) in raw)
                if (value.Length == 3 && long.TryParse(value[0], out var size) && long.TryParse(value[1], out var mtime))
                    result[key] = new CacheEntry(size, mtime, value[2]);
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private sealed record CacheEntry(long Size, long MtimeMs, string Hash);
}
