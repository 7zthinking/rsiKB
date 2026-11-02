using KBEA.Core.Diff;
using KBEA.Core.Files;
using KBEA.Core.Plugins;

namespace KBEA.Tests;

public sealed class FileAndPluginTests
{
    [Fact]
    public void Browser_HidesMetadataAndReadsTextPreview()
    {
        using var directory = new TestDirectory();
        var mount = directory.SubDirectory("mount");
        Directory.CreateDirectory(Path.Combine(mount, ".git"));
        Directory.CreateDirectory(Path.Combine(mount, ".7z"));
        Directory.CreateDirectory(Path.Combine(mount, "docs"));
        File.WriteAllText(Path.Combine(mount, "readme.md"), "知识库");
        var service = new FileBrowserService();
        var entries = service.GetEntries(mount, mount);
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, x => x.Name.StartsWith('.'));
        Assert.Equal("知识库", service.ReadTextPreview(Path.Combine(mount, "readme.md")));
    }

    [Fact]
    public void Browser_SearchesLocalDirectoriesRecursively()
    {
        using var directory = new TestDirectory();
        var mount = directory.SubDirectory("mount");
        var docs = directory.SubDirectory("mount/docs");
        File.WriteAllText(Path.Combine(docs, "release-notes.md"), "notes");
        File.WriteAllText(Path.Combine(docs, "other.txt"), "other");
        var service = new FileBrowserService();
        var result = service.SearchEntries(mount, mount, "notes");
        var entry = Assert.Single(result);
        Assert.Equal("release-notes.md", entry.Name);
        Assert.Equal(Path.Combine("docs", "release-notes.md"), entry.RelativePath);
    }

    [Fact]
    public void Diff_ProducesSideBySideChanges()
    {
        var result = new TextDiffService().Compare("first\nsecond", "first\nchanged");
        Assert.Equal(2, result.OldText.Lines.Count);
        Assert.Equal(2, result.NewText.Lines.Count);
        Assert.Contains(result.NewText.Lines, x => x.Text == "changed");
    }

    [Fact]
    public void PluginCatalog_DiscoversManifestWithoutLoadingCode()
    {
        using var directory = new TestDirectory();
        var plugin = directory.SubDirectory("sample");
        File.WriteAllText(Path.Combine(plugin, "plugin.json"), "{\"id\":\"sample\",\"name\":\"示例插件\",\"capabilities\":[\"assistant\"]}");
        var manifest = Assert.Single(new PluginCatalog().Discover(directory.Path));
        Assert.Equal("sample", manifest.Id);
        Assert.Equal("示例插件", manifest.Name);
    }
}
