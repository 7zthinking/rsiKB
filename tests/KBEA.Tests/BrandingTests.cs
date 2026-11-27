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
    public void Load_ReadsAccentColors()
    {
        using var directory = new TestDirectory();
        var branding = directory.SubDirectory("branding");
        File.WriteAllText(Path.Combine(branding, "branding.yaml"), """
            accent: "#3366FF"
            accent-soft: "#99AAEE"
            success: "#00AA55"
            warning: "#FF6600"
            """);

        var options = BrandingLoader.Load(branding);

        Assert.Equal("#3366FF", options.Accent);
        Assert.Equal("#99AAEE", options.AccentSoft);
        Assert.Equal("#00AA55", options.Success);
        Assert.Equal("#FF6600", options.Warning);
    }

    [Fact]
    public void Load_LeavesAccentColorsEmptyWhenNotConfigured()
    {
        using var directory = new TestDirectory();
        var branding = directory.SubDirectory("branding");
        File.WriteAllText(Path.Combine(branding, "branding.yaml"), "product-name: 只改名字");

        var options = BrandingLoader.Load(branding);

        Assert.Equal(string.Empty, options.Accent);
        Assert.Equal(string.Empty, options.AccentSoft);
        Assert.Equal(string.Empty, options.Success);
        Assert.Equal(string.Empty, options.Warning);
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

    [Theory]
    [InlineData("#3366FF", "#FF3366FF")]     // RRGGBB → 补 alpha
    [InlineData("3366FF", "#FF3366FF")]      // 可省略 #
    [InlineData("#36F", "#FF3366FF")]        // RGB 展开
    [InlineData("#803366FF", "#803366FF")]   // AARRGGBB 原样
    [InlineData("#FF3366FF", "#FF3366FF")]
    public void BrandingColor_ParsesSupportedFormats(string input, string expected)
    {
        Assert.True(BrandingColor.TryParse(input, out var color));
        Assert.Equal(expected, color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("#GGGGGG")]        // 非十六进制
    [InlineData("#12345")]        // 长度不对
    [InlineData("rgb(1,2,3)")]    // 不支持的颜色函数
    public void BrandingColor_RejectsInvalidValues(string? input)
    {
        Assert.False(BrandingColor.TryParse(input, out _));
    }

    [Fact]
    public void BrandingColor_PickFallsBackWhenInvalid()
    {
        Assert.Equal("#FFE87924", BrandingColor.Pick("#ZZZ", "#FFE87924"));
        Assert.Equal("#FF3366FF", BrandingColor.Pick("#3366FF", "#FFE87924"));
        Assert.Equal("#FFE87924", BrandingColor.Pick(null, "#FFE87924"));
    }
}
