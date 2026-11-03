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
                if (MigrateLegacy(configuration)) Save(configuration);
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

    /// <summary>把旧的「空间装目录」结构迁移为「目录装空间」：每个旧挂载点变成一个空间，旧分类目录变成逻辑目录。</summary>
    private static bool MigrateLegacy(AppConfiguration configuration)
    {
        if (configuration.Workspaces.Count == 0) return false;

        foreach (var legacy in configuration.Workspaces)
        {
            var directoryIds = new List<Guid>();
            void AddLegacyCategory(CategoryNode category, Guid? parentId)
            {
                var directory = new DirectoryDefinition { Name = category.Name, ParentId = parentId };
                configuration.Directories.Add(directory);
                directoryIds.Add(directory.Id);
                foreach (var child in category.Children) AddLegacyCategory(child, directory.Id);
            }
            foreach (var category in legacy.Categories) AddLegacyCategory(category, null);

            for (var index = 0; index < legacy.Mounts.Count; index++)
            {
                var mount = legacy.Mounts[index];
                configuration.Spaces.Add(new SpaceDefinition
                {
                    Name = string.IsNullOrWhiteSpace(mount.Name) ? Path.GetFileName(mount.Path) : mount.Name,
                    Path = mount.Path,
                    RemoteUrl = mount.RemoteUrl,
                    AutoSync = mount.AutoSync,
                    LastSyncAt = mount.LastSyncAt,
                    DirectoryId = index < directoryIds.Count ? directoryIds[index] : null
                });
            }
        }

        configuration.Workspaces.Clear();
        return true;
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
