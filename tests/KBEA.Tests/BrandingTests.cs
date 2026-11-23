using KBEA.App.Branding;

namespace KBEA.Tests;

/// <summary>
/// 品牌固化配置测试：branding/branding.yaml 的解析、兜底与命名约定。
/// </summary>
public sealed class BrandingTests
{
    [Fact]
    public void Load_ReadsProductNameEditionAndLogoSettings()
    {
        using var directory = new TestDirectory();
        var branding = directory.SubDirectory("branding");
        File.WriteAllText(Path.Combine(branding, "branding.yaml"), """
            product-name: 我的知识库
            edition: 专业版
            logo-file-name: custom.png
            logo-size: 42
            tagline: 一句介绍
            """);

        var options = BrandingLoader.Load(branding);

        Assert.Equal("我的知识库", options.ProductName);
        Assert.Equal("专业版", options.Edition);
        Assert.Equal("custom.png", options.LogoFileName);
        Assert.Equal(42, options.LogoSize);
        Assert.Equal("一句介绍", options.Tagline);
        Assert.Equal("我的知识库 · 专业版", options.WindowTitle);
    }

    [Fact]
    public void Load_FallsBackToDefaultsWhenFileMissing()
    {
        using var directory = new TestDirectory();
        var branding = directory.SubDirectory("branding");

        var options = BrandingLoader.Load(branding);

        Assert.Equal(BrandingOptions.DefaultProductName, options.ProductName);
        Assert.Equal(BrandingOptions.DefaultEdition, options.Edition);
        Assert.Equal("logo.png", options.LogoFileName);
        Assert.Null(BrandingLoader.FindConfigPath(branding));
    }

    [Fact]
    public void Load_FallsBackToDefaultsWhenYamlIsBroken()
    {
        using var directory = new TestDirectory();
        var branding = directory.SubDirectory("branding");
        File.WriteAllText(Path.Combine(branding, "branding.yaml"), "product-name: [未闭合\n  - 坏内容");

        var options = BrandingLoader.Load(branding);

        Assert.Equal(BrandingOptions.DefaultProductName, options.ProductName);
    }

    [Fact]
    public void Load_IgnoresUnknownKeysAndKeepsDefaults()
    {
        using var directory = new TestDirectory();
        var branding = directory.SubDirectory("branding");
        File.WriteAllText(Path.Combine(branding, "branding.yaml"), """
            product-name: 换个名字
            unknown-key: whatever
            """);

        var options = BrandingLoader.Load(branding);

        Assert.Equal("换个名字", options.ProductName);
    }

    [Fact]
    public void FindLogoPath_ReturnsNullWhenFileAbsent()
    {
        using var directory = new TestDirectory();
        var branding = directory.SubDirectory("branding");
        var options = new BrandingOptions { LogoFileName = "logo.png" };

        Assert.Null(BrandingLoader.FindLogoPath(branding, options));

        File.WriteAllText(Path.Combine(branding, "logo.png"), "fake");
        Assert.NotNull(BrandingLoader.FindLogoPath(branding, options));
    }
}
