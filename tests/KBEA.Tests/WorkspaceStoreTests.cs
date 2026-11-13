using System.Text.Json;
using KBEA.Core.Models;
using KBEA.Core.Storage;

namespace KBEA.Tests;

public sealed class WorkspaceStoreTests
{
    [Fact]
    public void Load_CreatesEmptyConfigurationAndPersistsChanges()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "config", "workspaces.json");
        var store = new WorkspaceStore(path);
        var configuration = store.Load();
        Assert.Empty(configuration.Spaces);
        configuration.AuthorName = "测试用户";
        configuration.Spaces.Add(new SpaceDefinition { Name = "研发资料", Path = directory.Path });
        store.Save(configuration);

        var restored = new WorkspaceStore(path).Load();
        Assert.Equal("测试用户", restored.AuthorName);
        var space = Assert.Single(restored.Spaces);
        Assert.Equal("研发资料", space.Name);
        Assert.Equal(directory.Path, space.Path);
        Assert.Empty(restored.Directories);
    }

    [Fact]
    public void Load_MigratesLegacyWorkspaceStructureToSpaces()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "workspaces.json");
        var legacy = new AppConfiguration();
        legacy.Workspaces.Add(new WorkspaceDefinition
        {
            Name = "研发资料",
            Mounts = { new MountDefinition { Name = "项目A", Path = directory.Path } }
        });
        File.WriteAllText(path, JsonSerializer.Serialize(legacy));

        var restored = new WorkspaceStore(path).Load();
        var space = Assert.Single(restored.Spaces);
        Assert.Equal("项目A", space.Name); // 挂载点名升级为空间名
        Assert.Equal(directory.Path, space.Path);
        Assert.Empty(restored.Workspaces);
    }

    [Fact]
    public void Load_BrokenJsonIsBackedUpAndRebuilt()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "workspaces.json");
        File.WriteAllText(path, "{broken");
        var restored = new WorkspaceStore(path).Load();
        Assert.Empty(restored.Workspaces);
        Assert.Single(Directory.GetFiles(directory.Path, "*.broken-*"));
    }
}
