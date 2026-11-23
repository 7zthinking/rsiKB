using KBEA.Core.Knowledge;
using KBEA.Core.Plugins;

namespace KBEA.Tests;

public sealed class PluginLoaderTests
{
    [Fact]
    public void PluginCatalog_DiscoverWithDirectories_ReturnsManifestWithDirectory()
    {
        using var directory = new TestDirectory();
        var plugin = directory.SubDirectory("sample");
        File.WriteAllText(Path.Combine(plugin, "plugin.json"),
            "{\"id\":\"sample\",\"name\":\"示例插件\",\"entryAssembly\":\"KBEA.Plugin.Sample.dll\",\"capabilities\":[\"toolbar\",\"metadata\"],\"minHostVersion\":\"0.3.0\"}");
        var found = Assert.Single(new PluginCatalog().DiscoverWithDirectories(directory.Path));
        Assert.Equal("sample", found.Manifest.Id);
        Assert.Equal("示例插件", found.Manifest.Name);
        Assert.Equal(plugin, found.Directory);
        Assert.True(found.Manifest.HasCapability("Toolbar")); // 能力名大小写不敏感
        Assert.False(found.Manifest.HasCapability("git"));
    }

    [Theory]
    [InlineData("", "0.3.0", true)]      // 空 = 不限制
    [InlineData("0.3.0", "0.3.0", true)]
    [InlineData("0.3", "0.3.0", true)]   // 前缀比较
    [InlineData("v0.3", "0.3.0", true)]  // 容忍 v 前缀
    [InlineData("0.3.0", "0.4.1", true)]
    [InlineData("0.4.0", "0.3.0", false)]
    [InlineData("1.0.0", "0.9.9", false)]
    [InlineData("not-a-version", "0.3.0", false)] // 无法解析按不兼容处理
    public void PluginLoader_JudgesHostVersionCompatibility(string minHostVersion, string hostVersion, bool expected)
    {
        Assert.Equal(expected, PluginLoader.IsHostVersionCompatible(minHostVersion, hostVersion));
    }

    [Fact]
    public void PluginLoader_SkipsDisabledPluginWithoutLoadingCode()
    {
        using var directory = new TestDirectory();
        var manifest = new PluginManifest { Id = "sample", Enabled = false, EntryAssembly = "missing.dll" };
        var result = Assert.Single(new PluginLoader().Load([(manifest, directory.Path)], new TestPluginHost()));
        Assert.Null(result.Instance);
        Assert.Null(result.Error);
        Assert.NotNull(result.SkipReason);
    }

    [Fact]
    public void PluginLoader_SkipsVersionIncompatiblePlugin()
    {
        using var directory = new TestDirectory();
        var manifest = new PluginManifest { Id = "sample", Enabled = true, MinHostVersion = "9.9.9", EntryAssembly = "missing.dll" };
        var result = Assert.Single(new PluginLoader().Load([(manifest, directory.Path)], new TestPluginHost()));
        Assert.Null(result.Instance);
        Assert.Null(result.Error);
        Assert.Contains("9.9.9", result.SkipReason);
    }

    [Fact]
    public void PluginLoader_RecordsReasonForMissingEntryAssembly()
    {
        using var directory = new TestDirectory();
        var manifest = new PluginManifest { Id = "sample", Enabled = true, EntryAssembly = "not-exist.dll" };
        var result = Assert.Single(new PluginLoader().Load([(manifest, directory.Path)], new TestPluginHost()));
        Assert.Null(result.Instance);
        Assert.Null(result.Error);
        Assert.Contains("not-exist.dll", result.SkipReason);
    }

    [Fact]
    public void PluginCatalog_Discover_RecordsManifestSourcePath()
    {
        using var directory = new TestDirectory();
        var plugin = directory.SubDirectory("sample");
        var file = Path.Combine(plugin, "plugin.json");
        File.WriteAllText(file, "{\"id\":\"sample\",\"name\":\"示例插件\",\"enabled\":true}");
        var found = Assert.Single(new PluginCatalog().Discover(directory.Path));
        Assert.Equal(file, found.SourcePath);
    }

    [Fact]
    public void PluginCatalog_SetEnabled_UpdatesManifestAndPreservesOtherFields()
    {
        using var directory = new TestDirectory();
        var plugin = directory.SubDirectory("sample");
        var file = Path.Combine(plugin, "plugin.json");
        File.WriteAllText(file,
            "{\n  \"id\": \"sample\",\n  \"name\": \"示例插件\",\n  \"version\": \"0.1.0\",\n  \"capabilities\": [\"toolbar\"],\n  \"enabled\": true\n}\n");

        var found = Assert.Single(new PluginCatalog().Discover(directory.Path));
        PluginCatalog.SetEnabled(found, false);
        Assert.False(found.Enabled);

        var text = File.ReadAllText(file);
        Assert.Contains("\"enabled\": false", text);
        // 其余字段原样保留
        Assert.Contains("\"id\": \"sample\"", text);
        Assert.Contains("\"name\": \"示例插件\"", text);
        Assert.Contains("\"capabilities\": [\"toolbar\"]", text);

        // 重新读回确认落盘生效
        var reread = Assert.Single(new PluginCatalog().Discover(directory.Path));
        Assert.False(reread.Enabled);
    }

    [Fact]
    public void PluginCatalog_SetEnabled_InsertsFieldWhenMissing()
    {
        using var directory = new TestDirectory();
        var plugin = directory.SubDirectory("sample");
        var file = Path.Combine(plugin, "plugin.json");
        File.WriteAllText(file, "{\n  \"id\": \"sample\",\n  \"name\": \"示例插件\"\n}\n");

        var found = Assert.Single(new PluginCatalog().Discover(directory.Path));
        PluginCatalog.SetEnabled(found, false);

        var reread = Assert.Single(new PluginCatalog().Discover(directory.Path));
        Assert.False(reread.Enabled);
        Assert.Equal("sample", reread.Id);
    }

    [Fact]
    public void PluginCatalog_Discover_ToleratesUtf8Bom()
    {
        using var directory = new TestDirectory();
        var plugin = directory.SubDirectory("sample");
        // 记事本等编辑器保存的 UTF-8 BOM 文件：JsonSerializer 默认不接受
        File.WriteAllText(Path.Combine(plugin, "plugin.json"),
            "\uFEFF{\n  \"id\": \"sample\",\n  \"name\": \"示例插件\",\n  \"enabled\": true\n}\n",
            new System.Text.UTF8Encoding(false));
        var found = Assert.Single(new PluginCatalog().Discover(directory.Path));
        Assert.Equal("sample", found.Id);
    }

    /// <summary>加载测试用精简宿主上下文（与 WorkerPluginHost 同构：元数据可用、UI no-op）。</summary>
    private sealed class TestPluginHost : IPluginHostContext
    {
        public string HostVersion => "0.3.0";
        public IMetadataWorkbench Metadata { get; } = new MetadataWorkbench();
        public ISidebarState Sidebar { get; } = new EmptySidebarState();
        public IBrowserState Browser { get; } = new EmptyBrowserState();
        public IUiSurface Ui { get; } = new NoopUiSurface();
        public IGitUiCommands? GitUi => null;
        public IWikiUiCommands? WikiUi => null;
        public IArchiveUiCommands? ArchiveUi => null;

        private sealed class EmptySidebarState : ISidebarState
        {
            public SidebarSnapshot Current { get; } = new(string.Empty, null, 0);
            public event EventHandler? Changed { add { } remove { } }
        }

        private sealed class EmptyBrowserState : IBrowserState
        {
            public BrowserSnapshot Current { get; } = new(null, null, null, 0, null);
            public event EventHandler? Changed { add { } remove { } }
        }

        private sealed class NoopUiSurface : IUiSurface
        {
            public void AddToolbarGroup(ToolbarGroupVm group) { }
            public void RemoveToolbarGroup(ToolbarGroupVm group) { }
            public void SetGlobalStatus(string message) { }
        }
    }
}
