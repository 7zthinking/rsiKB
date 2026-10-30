using System.Text.Json;
using KBEA.Core.Models;
using KBEA.Core.Storage;

namespace KBEA.Tests;

public sealed class MetadataStoreTests
{
    [Fact]
    public void CatalogLink_SupportsMultipleCategoriesWithoutDuplicates()
    {
        using var directory = new TestDirectory();
        var mount = directory.SubDirectory("mount");
        File.WriteAllText(Path.Combine(mount, "report.md"), "# 验收报告");
        var store = new MetadataStore(mount);
        store.Initialize();
        var workspace = Guid.NewGuid();
        var project = Guid.NewGuid();
        var acceptance = Guid.NewGuid();
        store.AddLink(workspace, project, "report.md");
        store.AddLink(workspace, acceptance, "report.md");
        store.AddLink(workspace, project, "report.md");

        Assert.Equal(2, store.GetLinks(workspace).Count);
        Assert.Single(store.GetLinks(workspace, project));
        Assert.Single(store.GetLinks(workspace, acceptance));
        Assert.Equal(2, store.GetCategoryIds(workspace, "report.md").Count);
        Assert.False(string.IsNullOrWhiteSpace(store.GetLinks(workspace)[0].FileHash));
    }

    [Fact]
    public void IndexAndSearch_ReturnsMatchingFilesAndSkipsMetadata()
    {
        using var directory = new TestDirectory();
        var mount = directory.SubDirectory("mount");
        Directory.CreateDirectory(Path.Combine(mount, "docs"));
        File.WriteAllText(Path.Combine(mount, "docs", "quarterly-report.md"), "content");
        File.WriteAllText(Path.Combine(mount, "notes.txt"), "content");
        var store = new MetadataStore(mount);
        store.Initialize();
        File.WriteAllText(Path.Combine(mount, ".7z", "secret.txt"), "not indexed");

        Assert.Equal(3, store.IndexFiles()); // Includes .gitignore, excludes .7z content.
        var result = Assert.Single(store.Search("quarterly"));
        Assert.Equal("quarterly-report.md", result.Name);
        Assert.DoesNotContain(store.Search("secret"), x => x.Name == "secret.txt");
    }

    [Fact]
    public void SnapshotAndArchive_AreStoredWithMountedFolder()
    {
        using var directory = new TestDirectory();
        var mount = directory.SubDirectory("mount");
        var store = new MetadataStore(mount);
        store.Initialize();
        var workspace = new WorkspaceDefinition { Name = "空间A" };
        var snapshot = store.ExportCatalogSnapshot(workspace);
        Assert.True(File.Exists(snapshot));
        using var json = JsonDocument.Parse(File.ReadAllText(snapshot));
        Assert.True(json.RootElement.TryGetProperty("Categories", out _));

        var record = new ArchiveRecord("1", "项目A", "20261030-180000", "D:/archive", "验收", DateTimeOffset.Now, "完成");
        store.RecordArchive(record);
        Assert.Equal("验收", Assert.Single(store.GetArchives()).Note);
    }
}
