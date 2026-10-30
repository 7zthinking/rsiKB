using System.Text.Json;

namespace KBEA.Core.Plugins;

public interface IAssistantProvider { string DisplayName { get; } }
public interface IReportProvider { string DisplayName { get; } }
public interface IProjectMgmtProvider { string DisplayName { get; } }
public interface IIndexEngine { Task RebuildAsync(string mountPath, CancellationToken cancellationToken = default); }
public interface ICatalogRuleSource { string DisplayName { get; } }
public interface IInfoCardProvider { string DisplayName { get; } }

public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string EntryAssembly { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public sealed class PluginCatalog
{
    public IReadOnlyList<PluginManifest> Discover(string? pluginRoot = null)
    {
        pluginRoot ??= Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginRoot)) return [];
        var result = new List<PluginManifest>();
        foreach (var file in Directory.EnumerateFiles(pluginRoot, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id)) result.Add(manifest);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken optional plugin must not prevent the open-source core from starting.
            }
        }
        return result;
    }
}
