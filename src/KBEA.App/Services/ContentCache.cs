using System.IO;

namespace KBEA.App.Services;

/// <summary>
/// 内容寻址缓存（LRU）：预览/打开过的团队共享文件按 SHA-256 键存本地，
/// 二次打开零网络。默认限额 2GB，超限按最后访问时间淘汰。
/// </summary>
public sealed class ContentCache
{
    public const long DefaultLimitBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _root;
    private readonly long _limitBytes;
    private readonly object _sync = new();

    public ContentCache(long limitBytes = DefaultLimitBytes, string? rootPath = null)
    {
        _limitBytes = limitBytes;
        _root = rootPath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KBEA", "TeamCache");
    }

    /// <summary>缓存键：owner 机器码 + 空间 + 路径 + 大小 + mtime。</summary>
    public string Key(string ownerMachineCode, Guid spaceId, string path, long size, long mtime)
    {
        var raw = $"{ownerMachineCode}|{spaceId}|{path}|{size}|{mtime}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>命中返回本地缓存路径，并刷新访问时间。</summary>
    public string? TryGet(string key)
    {
        var path = System.IO.Path.Combine(_root, key);
        if (!File.Exists(path)) return null;
        lock (_sync)
        {
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
        }
        return path;
    }

    /// <summary>写入缓存（bytes 已由调用方下载），返回本地路径。</summary>
    public string Put(string key, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        var path = System.IO.Path.Combine(_root, key);
        File.WriteAllBytes(path, bytes);
        Trim();
        return path;
    }

    /// <summary>LRU 淘汰：总量超限则按最后访问时间从旧到新删。</summary>
    private void Trim()
    {
        if (_limitBytes <= 0) return;
        try
        {
            var files = Directory.EnumerateFiles(_root)
                .Select(f => new FileInfo(f) { })
                .Where(f => f.Exists)
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();
            long total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= _limitBytes) break;
                total -= file.Length;
                try { file.Delete(); }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存清理失败不影响主流程
        }
    }
}
