using KBEA.Core.Plugins;

namespace KBEA.Tests;

/// <summary>
/// 插件启用策略测试：发行方默认（plugins.defaults.json）与用户显式设置的优先级。
/// </summary>
public sealed class PluginEnablePolicyTests
{
    [Fact]
    public void Policy_DisabledListWinsOverEnabledList()
    {
        var policy = new PluginEnablePolicy
        {
            Enabled = ["a", "b"],
            Disabled = ["b"]
        };

        Assert.True(policy.GetDefaultEnabled("a"));
        Assert.False(policy.GetDefaultEnabled("b"));
    }

    [Fact]
    public void Policy_UnmentionedPluginReturnsNull()
    {
        var policy = new PluginEnablePolicy { Enabled = ["a"] };

        Assert.Null(policy.GetDefaultEnabled("other"));
        Assert.Null(policy.GetDefaultEnabled(""));
    }

    [Fact]
    public void Policy_LoadMissingFileReturnsEmptyPolicy()
    {
        using var directory = new TestDirectory();

        var policy = PluginEnablePolicy.Load(Path.Combine(directory.Path, "not-exist.json"));

        Assert.Empty(policy.Enabled);
        Assert.Empty(policy.Disabled);
    }

    [Fact]
    public void Policy_LoadBrokenFileReturnsEmptyPolicy()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "plugins.defaults.json");
        File.WriteAllText(path, "{ not json");

        var policy = PluginEnablePolicy.Load(path);

        Assert.Empty(policy.Enabled);
        Assert.Empty(policy.Disabled);
    }

    [Fact]
    public void Policy_LoadReadsEnabledAndDisabledLists()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "plugins.defaults.json");
        File.WriteAllText(path, """{"enabled":["p1"],"disabled":["p2"]}""");

        var policy = PluginEnablePolicy.Load(path);

        Assert.True(policy.GetDefaultEnabled("p1"));
        Assert.False(policy.GetDefaultEnabled("p2"));
    }

    [Fact]
    public void SetEnabled_MarksUserChoiceAndWritesFlagToManifest()
    {
        using var directory = new TestDirectory();
        var plugin = directory.SubDirectory("sample");
        var manifestFile = Path.Combine(plugin, "plugin.json");
        File.WriteAllText(manifestFile, "{\"id\":\"sample\",\"name\":\"示例\",\"enabled\":true}");
        var manifest = Assert.Single(new PluginCatalog().Discover(directory.Path));

        PluginCatalog.SetEnabled(manifest, false);

        Assert.False(manifest.Enabled);
        Assert.True(manifest.EnabledByUser);
        Assert.Equal(PluginEnableSource.UserDisabled, manifest.EnableSource);
        var text = File.ReadAllText(manifestFile);
        Assert.Contains("\"enabled\": false", text);
        Assert.Contains("\"enabledByUser\": true", text);
    }

    [Fact]
    public void ResetToDefault_ClearsUserChoice()
    {
        using var directory = new TestDirectory();
        var plugin = directory.SubDirectory("sample");
        var manifestFile = Path.Combine(plugin, "plugin.json");
        File.WriteAllText(manifestFile, "{\"id\":\"sample\",\"name\":\"示例\",\"enabled\":true}");
        var manifest = Assert.Single(new PluginCatalog().Discover(directory.Path));

        PluginCatalog.SetEnabled(manifest, false);
        PluginCatalog.ResetToDefault(manifest);

        Assert.False(manifest.EnabledByUser);
        // 没有发行方策略时回到插件清单自带的默认值
        Assert.True(manifest.Enabled);
        Assert.Equal(PluginEnableSource.Default, manifest.EnableSource);
        Assert.Contains("\"enabledByUser\": false", File.ReadAllText(manifestFile));
    }

    [Fact]
    public void Discover_AppliesPolicyAndKeepsUserChoice()
    {
        using var directory = new TestDirectory();
        var plugins = directory.SubDirectory("plugins");

        // 发行方策略：p1 默认启用、p2 默认禁用
        var policyPath = Path.Combine(directory.Path, "plugins.defaults.json");
        File.WriteAllText(policyPath, """{"enabled":["p1"],"disabled":["p2"]}""");

        // p1：策略生效 → 启用
        var p1Dir = Path.Combine(plugins, "p1");
        Directory.CreateDirectory(p1Dir);
        File.WriteAllText(Path.Combine(p1Dir, "plugin.json"), "{\"id\":\"p1\",\"name\":\"P1\",\"enabled\":false}");

        // p2：策略生效 → 禁用
        var p2Dir = Path.Combine(plugins, "p2");
        Directory.CreateDirectory(p2Dir);
        File.WriteAllText(Path.Combine(p2Dir, "plugin.json"), "{\"id\":\"p2\",\"name\":\"P2\",\"enabled\":true}");

        // p3：策略未提及 → 沿用清单自带值
        var p3Dir = Path.Combine(plugins, "p3");
        Directory.CreateDirectory(p3Dir);
        File.WriteAllText(Path.Combine(p3Dir, "plugin.json"), "{\"id\":\"p3\",\"name\":\"P3\",\"enabled\":true}");

        // p4：用户已显式设置 → 优先于策略
        var p4Dir = Path.Combine(plugins, "p4");
        Directory.CreateDirectory(p4Dir);
        File.WriteAllText(Path.Combine(p4Dir, "plugin.json"), "{\"id\":\"p4\",\"name\":\"P4\",\"enabled\":true,\"enabledByUser\":true}");

        var manifests = new PluginCatalog().Discover(plugins).ToDictionary(m => m.Id);
        PluginCatalog.OverridePolicyForTests(policyPath);

        try
        {
            var discovered = new PluginCatalog().Discover(plugins).ToDictionary(m => m.Id);
            Assert.True(discovered["p1"].Enabled);
            Assert.Equal(PluginEnableSource.PolicyEnabled, discovered["p1"].EnableSource);
            Assert.False(discovered["p2"].Enabled);
            Assert.Equal(PluginEnableSource.PolicyDisabled, discovered["p2"].EnableSource);
            Assert.True(discovered["p3"].Enabled);
            Assert.Equal(PluginEnableSource.Default, discovered["p3"].EnableSource);
            Assert.True(discovered["p4"].Enabled);
            Assert.Equal(PluginEnableSource.UserEnabled, discovered["p4"].EnableSource);
        }
        finally
        {
            PluginCatalog.OverridePolicyForTests(null);
        }

        // 无策略时全部沿用清单自带值
        Assert.False(manifests["p1"].Enabled);
        Assert.True(manifests["p2"].Enabled);
    }
}
