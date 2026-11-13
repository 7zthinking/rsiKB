using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KBEA.Core.Plugins;

namespace KBEA.Core.Services;

/// <summary>
/// 遍历工作空间文件，通过 PluginManager 查找匹配插件，产出 FileMetadata JSON 到 .7z/catalog 目录。
/// 采用文件 SHA256 作为目录中的主键。
/// </summary>
public sealed class MetadataScanningService
{
    public static MetadataScanningService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private MetadataScanningService() { }

    /// <summary>扫描工作空间全部文件，将可识别的文件元数据写入 catalog/{hash}.json。</summary>
    public int ScanWorkspace(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Directory.Exists(workspaceRoot)) return 0;

        var catalogDir = Path.Combine(workspaceRoot, ".7z", "catalog");
        Directory.CreateDirectory(catalogDir);

        var count = 0;
        foreach (var file in EnumerateFiles(workspaceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (ScanFile(file, catalogDir)) count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单文件失败不应阻断整体扫描
            }
        }
        return count;
    }

    /// <summary>扫描单个文件，若有匹配插件则将 FileMetadata 以 {hash}.json 写入 catalogDir。</summary>
    public bool ScanFile(string filePath, string catalogDir)
    {
        var ext = Path.GetExtension(filePath);
        var plugin = PluginManager.Instance.GetPluginForExtension(ext);
        if (plugin is null) return false;

        var metadata = plugin.Scan(filePath);
        var hash = ComputeSha256(filePath);
        var outPath = Path.Combine(catalogDir, $"{hash}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(metadata, JsonOptions));
        return true;
    }

    /// <summary>读取 catalogDir 中已扫描的 FileMetadata（按 hash）。</summary>
    public FileMetadata? LoadFromCatalog(string hash, string catalogDir)
    {
        var path = Path.Combine(catalogDir, $"{hash}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FileMetadata>(json, JsonOptions);
        }
        catch { return null; }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToArray(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    var name = Path.GetFileName(entry);
                    if (!name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                        pending.Push(entry);
                }
                else yield return entry;
            }
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes);
    }
}
