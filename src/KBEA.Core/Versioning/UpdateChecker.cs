using System.Text.Json;

namespace KBEA.Core.Versioning;

/// <summary>检查更新的结果。</summary>
public sealed record UpdateCheckResult(
    /// <summary>最新可用版本号（tag）；没有可用版本时为 null。</summary>
    string? LatestVersion,
    /// <summary>最新版本是否为预发布版。</summary>
    bool IsPrerelease,
    /// <summary>最新版本的下载页地址。</summary>
    string? ReleaseUrl,
    /// <summary>给用户看的一句话结论。</summary>
    string Message,
    /// <summary>结论类型。</summary>
    UpdateStatus Status);

/// <summary>检查更新的结论类型。</summary>
public enum UpdateStatus
{
    /// <summary>已是最新。</summary>
    UpToDate,

    /// <summary>有正式版新版本可用。</summary>
    NewVersion,

    /// <summary>有预发布版可用（正式版已是最新）。</summary>
    NewPrerelease,

    /// <summary>本地版本领先于远程（本地是开发版/预发布版）。</summary>
    Ahead,

    /// <summary>无法获取远程信息。</summary>
    Failed
}

/// <summary>
/// 更新检查：读 GitHub Releases，与本地版本比较。
/// 放在 Core 里（不依赖 WPF），便于单测。
/// </summary>
public static class UpdateChecker
{
    /// <summary>仓库地址（与 README 一致）。</summary>
    public const string Repository = "7zthinking/rsiKB";

    /// <summary>Releases 列表接口（包含预发布版与草稿，需要自己过滤 draft）。</summary>
    public const string ReleasesApiUrl = $"https://api.github.com/repos/{Repository}/releases?per_page=30";

    /// <summary>
    /// 从 GitHub Releases JSON 中挑出最新可用版本。
    /// 规则：跳过 draft；正式版优先于预发布版；同类型取版本号最大的。
    /// 若正式版不比本地新、但有预发布版比本地新，则提示预发布版。
    /// </summary>
    public static UpdateCheckResult Evaluate(string releasesJson, string currentVersion)
    {
        List<ReleaseInfo> releases;
        try
        {
            releases = ParseReleases(releasesJson);
        }
        catch (JsonException ex)
        {
            return new UpdateCheckResult(null, false, null, $"无法解析远程版本信息：{ex.Message}", UpdateStatus.Failed);
        }

        var candidates = releases.Where(r => !r.Draft).ToList();
        if (candidates.Count == 0)
            return new UpdateCheckResult(null, false, null, "GitHub 上还没有发布任何版本。", UpdateStatus.Failed);

        var stable = candidates.Where(r => !r.Prerelease).ToList();
        var latestStable = stable.Count > 0 ? Newest(stable) : null;

        // 正式版优先：仅当正式版不比本地新（持平或更旧）时，才看有没有更新的预发布版
        var chosen = latestStable;
        if (chosen is null || CompareToLocal(currentVersion, chosen) >= 0)
        {
            var prereleases = candidates.Where(r => r.Prerelease).ToList();
            var newestPre = prereleases.Count > 0 ? Newest(prereleases) : null;
            if (newestPre is not null && CompareToLocal(currentVersion, newestPre) < 0)
                chosen = newestPre;
        }

        if (chosen is null)
        {
            // 只有正式版且都不比本地新 → 已是最新
            var newest = Newest(candidates)!;
            return new UpdateCheckResult(newest.Tag, newest.Prerelease, newest.Url,
                $"已是最新版本 {currentVersion}。", UpdateStatus.UpToDate);
        }

        var comparison = CompareToLocal(currentVersion, chosen);
        var message = comparison switch
        {
            < 0 => chosen.Prerelease
                ? $"发现新版本 {chosen.Tag}（预发布，当前 {currentVersion}）。"
                : $"发现新版本 {chosen.Tag}（当前 {currentVersion}）。请到 GitHub 下载最新版。",
            0 => $"已是最新版本 {currentVersion}。",
            _ => $"当前版本 {currentVersion} 领先于远程 {chosen.Tag}，可能是开发版。"
        };

        var status = comparison switch
        {
            < 0 => chosen.Prerelease ? UpdateStatus.NewPrerelease : UpdateStatus.NewVersion,
            0 => UpdateStatus.UpToDate,
            _ => UpdateStatus.Ahead
        };

        return new UpdateCheckResult(chosen.Tag, chosen.Prerelease, chosen.Url, message, status);
    }

    /// <summary>取版本号最大的一条。</summary>
    private static ReleaseInfo Newest(List<ReleaseInfo> list) =>
        list.OrderByDescending(r => r.Version, Comparer<ParsedVersion?>.Create((a, b) =>
            a is null ? (b is null ? 0 : -1) : b is null ? 1 : a.CompareTo(b)))
            .First();

    /// <summary>本地版本与远程版本比较：本地 &lt; 远程 返回负数。</summary>
    private static int CompareToLocal(string currentVersion, ReleaseInfo remote) =>
        remote.Version is null
            ? VersionComparer.Compare(currentVersion, remote.Tag)
            : CompareParsed(currentVersion, remote.Version);

    /// <summary>比较本地版本串与已解析的远程版本。</summary>
    private static int CompareParsed(string currentVersion, ParsedVersion remote)
    {
        var local = VersionComparer.Parse(currentVersion);
        if (local is null) return -1;
        return local.CompareTo(remote);
    }

    /// <summary>解析 Releases JSON 为 ReleaseInfo 列表。</summary>
    public static List<ReleaseInfo> ParseReleases(string json)
    {
        var result = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var tag = item.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var url = item.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
            var draft = item.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True;
            var prerelease = item.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True;
            result.Add(new ReleaseInfo(tag, VersionComparer.Parse(tag), draft, prerelease, url));
        }
        return result;
    }
}

/// <summary>一条 Release 记录。</summary>
public sealed record ReleaseInfo(
    string Tag,
    ParsedVersion? Version,
    bool Draft,
    bool Prerelease,
    string? Url);
