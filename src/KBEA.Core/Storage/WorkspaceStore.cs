using System.Text.Json;
using KBEA.Core.Models;

namespace KBEA.Core.Storage;

public sealed class WorkspaceStore
{
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _sync = new();

    public WorkspaceStore(string? configPath = null)
    {
        ConfigPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KBEA",
            "workspaces.json");
    }

    public string ConfigPath { get; }

    public AppConfiguration Load()
    {
        lock (_sync)
        {
            if (!File.Exists(ConfigPath))
            {
                var initial = new AppConfiguration();
                Save(initial);
                return initial;
            }

            try
            {
                var json = File.ReadAllText(ConfigPath);
                var configuration = JsonSerializer.Deserialize<AppConfiguration>(json, _jsonOptions) ?? new AppConfiguration();
                configuration.Workspaces.RemoveAll(workspace =>
                    workspace.Name == "默认工作空间" &&
                    workspace.Mounts.Count == 0 &&
                    workspace.Favorites.Count == 0 &&
                    workspace.Categories.Select(x => x.Name).SequenceEqual(["项目", "专业", "阶段"]));
                return configuration;
            }
            catch (JsonException)
            {
                var backup = ConfigPath + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(ConfigPath, backup, overwrite: true);
                var initial = new AppConfiguration();
                Save(initial);
                return initial;
            }
        }
    }

    public void Save(AppConfiguration configuration)
    {
        lock (_sync)
        {
            var directory = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(directory);
            var temporary = ConfigPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(configuration, _jsonOptions));
            File.Move(temporary, ConfigPath, overwrite: true);
        }
    }
}
