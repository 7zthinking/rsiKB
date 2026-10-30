using KBEA.Core.Models;
using KBEA.Core.Storage;

namespace KBEA.Tests;

public sealed class WorkspaceStoreTests
{
    [Fact]
    public void Load_CreatesDefaultWorkspaceAndPersistsChanges()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "config", "workspaces.json");
        var store = new WorkspaceStore(path);
        var configuration = store.Load();
        configuration.AuthorName = "测试用户";
        configuration.Workspaces[0].Name = "研发资料";
        configuration.Workspaces[0].Mounts.Add(new MountDefinition { Name = "项目A", Path = directory.Path });
        store.Save(configuration);

        var restored = new WorkspaceStore(path).Load();
        Assert.Equal("测试用户", restored.AuthorName);
        Assert.Equal("研发资料", Assert.Single(restored.Workspaces).Name);
        Assert.Equal("项目A", Assert.Single(restored.Workspaces[0].Mounts).Name);
        Assert.Equal(3, restored.Workspaces[0].Categories.Count);
    }

    [Fact]
    public void Load_BrokenJsonIsBackedUpAndRebuilt()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "workspaces.json");
        File.WriteAllText(path, "{broken");
        var restored = new WorkspaceStore(path).Load();
        Assert.Single(restored.Workspaces);
        Assert.Single(Directory.GetFiles(directory.Path, "*.broken-*"));
    }
}
