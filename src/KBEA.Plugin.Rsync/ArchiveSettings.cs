using System.Text.Json;

namespace KBEA.Plugin.Rsync;

/// <summary>
/// 插件自有配置：rsync 可执行路径与归档根目录。
/// 插件设置项的持久化由插件自行负责（插件契约 v1 不含宿主配置存储），
/// 这里写到 %AppData%\KBEA\plugins\kbea-plugin-rsync\settings.json。
/// </summary>
public sealed class ArchiveSettings
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KBEA", "plugins", "kbea-plugin-rsync");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    /// <summary>rsync 可执行文件路径（相对路径按程序目录解析）。</summary>
    public string RsyncExecutable { get; set; } = "tools/rsync/rsync.exe";

    /// <summary>归档根目录（留档的目标根）。</summary>
    public string ArchiveRoot { get; set; } = string.Empty;

    /// <summary>归档超时（小时）。</summary>
    public int TimeoutHours { get; set; } = 2;

    private static ArchiveSettings? _cached;

    /// <summary>读配置（首次读时从磁盘加载，之后走缓存；外部改动需调用 Reload）。</summary>
    public static ArchiveSettings Load()
    {
        if (_cached is not null) return _cached;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _cached = JsonSerializer.Deserialize<ArchiveSettings>(json) ?? new ArchiveSettings();
            }
            else
            {
                _cached = new ArchiveSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _cached = new ArchiveSettings();
        }
        return _cached;
    }

    /// <summary>保存配置到磁盘。</summary>
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
        _cached = this;
    }

    /// <summary>丢弃缓存，下次 Load 时重新读盘。</summary>
    public static void Reload() => _cached = null;
}
