using KBEA.Core.Versioning;

namespace KBEA.Tests;

/// <summary>
/// 版本统一与检查更新逻辑测试：
/// 版本号唯一真相源、版本解析与比较、GitHub Releases 评估。
/// </summary>
public sealed class VersioningTests
{
    // ===== 版本号唯一真相源 =====

    [Fact]
    public void AppVersion_MatchesAssemblyInformationalVersion()
    {
        // 程序集版本来自 Directory.Build.props 的 <Version>
        var expected = typeof(AppVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .First()
            .InformationalVersion
            .Split('+')[0];

        Assert.Equal(expected, AppVersion.Version);
        Assert.StartsWith("v", AppVersion.Display);
        Assert.Equal("v" + AppVersion.Version, AppVersion.Display);
    }

    [Fact]
    public void AppVersion_IsNotHardcodedInMultiplePlaces()
    {
        // 宿主版本常量（App 侧）与 Core 的 AppVersion 必须一致
        Assert.Equal(AppVersion.Version, KBEA.Core.Versioning.AppVersion.Version);
    }

    // ===== 版本解析 =====

    [Theory]
    [InlineData("0.4.0", 0, 4, 0, "")]
    [InlineData("v0.4.0", 0, 4, 0, "")]
    [InlineData("V0.4.0", 0, 4, 0, "")]
    [InlineData("0.4", 0, 4, 0, "")]
    [InlineData("1", 1, 0, 0, "")]
    [InlineData("0.4.0-beta", 0, 4, 0, "beta")]
    [InlineData("v1.2.3-beta.1", 1, 2, 3, "beta.1")]
    public void VersionComparer_ParsesSupportedFormats(string input, int major, int minor, int build, string prerelease)
    {
        var parsed = VersionComparer.Parse(input);

        Assert.NotNull(parsed);
        Assert.Equal(major, parsed!.Major);
        Assert.Equal(minor, parsed.Minor);
        Assert.Equal(build, parsed.Build);
        Assert.Equal(prerelease, parsed.Prerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1.x.3")]
    [InlineData("1.-2.3")]
    public void VersionComparer_RejectsInvalidVersions(string? input)
    {
        Assert.Null(VersionComparer.Parse(input));
    }

    // ===== 版本比较 =====

    [Theory]
    [InlineData("0.3.0", "0.4.0", -1)]
    [InlineData("0.4.0", "0.4.0", 0)]
    [InlineData("0.4.1", "0.4.0", 1)]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("v0.4.0", "0.4.0", 0)]
    [InlineData("0.4", "0.4.0", 0)]      // 缺省段位按 0 处理
    [InlineData("0.4.0-beta", "0.4.0", -1)] // 预发布版比正式版旧
    [InlineData("0.4.0-beta", "0.4.0-beta", 0)]
    [InlineData("0.4.0-alpha", "0.4.0-beta", -1)]
    public void VersionComparer_ComparesCorrectly(string a, string b, int expectedSign)
    {
        var result = VersionComparer.Compare(a, b);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("0.4.0+abc123", "0.4.0")]
    [InlineData(" 0.4.0 ", "0.4.0")]
    [InlineData("v0.4.0-beta+abc", "0.4.0-beta")]
    public void VersionComparer_NormalizesForDisplay(string input, string expected)
    {
        Assert.Equal(expected, VersionComparer.Normalize(input));
    }

    // ===== 检查更新 =====

    private const string ReleasesJson = """
        [
          { "tag_name": "v0.2.0", "draft": false, "prerelease": false, "html_url": "https://github.com/7zthinking/rsiKB/releases/tag/v0.2.0" },
          { "tag_name": "v0.4.0-beta.1", "draft": false, "prerelease": true, "html_url": "https://github.com/7zthinking/rsiKB/releases/tag/v0.4.0-beta.1" },
          { "tag_name": "v0.9.0", "draft": true, "prerelease": false, "html_url": "https://github.com/7zthinking/rsiKB/releases/tag/v0.9.0" }
        ]
        """;

    [Fact]
    public void UpdateChecker_PrefersStableOverPrereleaseAndSkipsDraft()
    {
        // 正式版 v0.5.0 版本号虽小于预发布 v0.6.0-beta.1，仍优先提示正式版
        var json = """
            [
              { "tag_name": "v0.6.0-beta.1", "draft": false, "prerelease": true, "html_url": "https://x/v0.6.0-beta.1" },
              { "tag_name": "v0.5.0", "draft": false, "prerelease": false, "html_url": "https://x/v0.5.0" }
            ]
            """;

        var result = UpdateChecker.Evaluate(json, "0.4.0");

        Assert.Equal("v0.5.0", result.LatestVersion);
        Assert.False(result.IsPrerelease);
        Assert.Equal(UpdateStatus.NewVersion, result.Status);
        Assert.Contains("v0.5.0", result.Message);
    }

    [Fact]
    public void UpdateChecker_ReportsUpToDateWhenSameVersion()
    {
        // 只有同版本正式版与一个草稿：草稿忽略 → 已是最新
        var json = """
            [
              { "tag_name": "v0.2.0", "draft": false, "prerelease": false, "html_url": "https://x/v0.2.0" },
              { "tag_name": "v0.9.0", "draft": true, "prerelease": false, "html_url": "https://x/v0.9.0" }
            ]
            """;

        var result = UpdateChecker.Evaluate(json, "0.2.0");

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Contains("已是最新版本", result.Message);
    }

    [Fact]
    public void UpdateChecker_SuggestsPrereleaseWhenStableIsOlderThanLocal()
    {
        // 本地 0.3.0 比最新正式版 v0.2.0 还新，但预发布 v0.4.0-beta.1 更新 → 提示预发布
        var result = UpdateChecker.Evaluate(ReleasesJson, "0.3.0");

        Assert.Equal("v0.4.0-beta.1", result.LatestVersion);
        Assert.Equal(UpdateStatus.NewPrerelease, result.Status);
        Assert.True(result.IsPrerelease);
    }

    [Fact]
    public void UpdateChecker_ReportsAheadWhenLocalIsNewer()
    {
        var result = UpdateChecker.Evaluate(ReleasesJson, "0.4.0");

        Assert.Equal(UpdateStatus.Ahead, result.Status);
        Assert.Contains("领先于远程", result.Message);
    }

    [Fact]
    public void UpdateChecker_ReportsPrereleaseWhenOnlyPrereleaseIsNewer()
    {
        // 本地已是 v0.2.0，只有 v0.4.0-beta.1 比它新
        var json = """
            [
              { "tag_name": "v0.2.0", "draft": false, "prerelease": false, "html_url": "https://x/v0.2.0" },
              { "tag_name": "v0.4.0-beta.1", "draft": false, "prerelease": true, "html_url": "https://x/v0.4.0-beta.1" }
            ]
            """;

        var result = UpdateChecker.Evaluate(json, "0.2.0");

        Assert.Equal(UpdateStatus.NewPrerelease, result.Status);
        Assert.True(result.IsPrerelease);
        Assert.Contains("预发布", result.Message);
    }

    [Fact]
    public void UpdateChecker_FailsOnEmptyReleaseList()
    {
        var result = UpdateChecker.Evaluate("[]", "0.4.0");

        Assert.Equal(UpdateStatus.Failed, result.Status);
    }

    [Fact]
    public void UpdateChecker_FailsOnBrokenJson()
    {
        var result = UpdateChecker.Evaluate("{ not json", "0.4.0");

        Assert.Equal(UpdateStatus.Failed, result.Status);
    }

    [Fact]
    public void UpdateChecker_PicksHighestVersionAmongStableReleases()
    {
        var json = """
            [
              { "tag_name": "v0.2.0", "draft": false, "prerelease": false },
              { "tag_name": "v0.4.0", "draft": false, "prerelease": false },
              { "tag_name": "v0.3.0", "draft": false, "prerelease": false }
            ]
            """;

        var result = UpdateChecker.Evaluate(json, "0.3.0");

        Assert.Equal("v0.4.0", result.LatestVersion);
        Assert.Equal(UpdateStatus.NewVersion, result.Status);
    }

    [Fact]
    public void UpdateChecker_ExtractsPortableZipAssetUrl()
    {
        // 从 assets 里找 KBEA-*-win-x64.zip 自动更新包（排除 .sha256）
        var json = """
            [
              { "tag_name": "v0.5.0", "draft": false, "prerelease": false,
                "assets": [
                  { "name": "KBEA-v0.5.0-win-x64.zip.sha256", "browser_download_url": "https://x/KBEA-v0.5.0-win-x64.zip.sha256" },
                  { "name": "KBEA-v0.5.0-win-x64.zip", "browser_download_url": "https://x/KBEA-v0.5.0-win-x64.zip" }
                ] }
            ]
            """;

        var result = UpdateChecker.Evaluate(json, "0.4.0");

        Assert.Equal(UpdateStatus.NewVersion, result.Status);
        Assert.Equal("https://x/KBEA-v0.5.0-win-x64.zip", result.AssetUrl);
    }
}
