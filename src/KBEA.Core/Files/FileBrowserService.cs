using System.Text;
using KBEA.Core.Models;

namespace KBEA.Core.Files;

public sealed class FileBrowserService
{
    public IReadOnlyList<FileEntry> GetEntries(string path, string rootPath)
    {
        if (!Directory.Exists(path)) return [];
        var result = new List<FileEntry>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                if (IsHiddenMetadata(directory)) continue;
                var info = new DirectoryInfo(directory);
                result.Add(new FileEntry(info.Name, info.FullName, Path.GetRelativePath(rootPath, info.FullName), true, 0, info.LastWriteTime));
            }
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var info = new FileInfo(file);
                result.Add(new FileEntry(info.Name, info.FullName, Path.GetRelativePath(rootPath, info.FullName), false, info.Length, info.LastWriteTime));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return result.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public IReadOnlyList<FileEntry> GetDirectories(string path, string rootPath) =>
        GetEntries(path, rootPath).Where(x => x.IsDirectory).ToArray();

    public string ReadTextPreview(string path, int maximumBytes = 262144)
    {
        var info = new FileInfo(path);
        if (info.Length > maximumBytes) return "文件较大，已省略预览。可双击使用系统默认程序打开。";
        var bytes = File.ReadAllBytes(path);
        if (bytes.Take(Math.Min(bytes.Length, 8000)).Any(b => b == 0)) return "这是二进制文件，暂不提供文本预览。";
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
    }

    private static bool IsHiddenMetadata(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".7z", StringComparison.OrdinalIgnoreCase);
    }
}
