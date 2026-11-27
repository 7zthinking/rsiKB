namespace KBEA.Core.Versioning;

/// <summary>
/// 版本号解析与比较：容忍 <c>v</c> 前缀、<c>-beta</c> 等预发布后缀、缺省段位。
/// 用于「检查更新」的版本比较。
/// </summary>
public static class VersionComparer
{
    /// <summary>解析版本号为数字段 + 预发布标记；无法解析返回 null。</summary>
    public static ParsedVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        // 拆预发布后缀：1.2.3-beta.1 → 主体 1.2.3，后缀 beta.1
        var dash = value.IndexOf('-');
        var prerelease = string.Empty;
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..].Trim();
            value = value[..dash];
        }

        // 主体段严格校验：空段（如 "1."、"1..2"）或非数字段一律拒绝
        var parts = value.Split('.');
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length) break;
            if (parts[i].Length == 0 || !int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0) return null;
        }
        return new ParsedVersion(numbers[0], numbers[1], numbers[2], prerelease);
    }

    /// <summary>
    /// 比较两个版本号：a &lt; b 返回负数，a == b 返回 0，a &gt; b 返回正数。
    /// 数字段优先；数字段相同时，<b>有预发布标记的版本视为更旧</b>（SemVer 惯例：
    /// 1.0.0-beta &lt; 1.0.0）。
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var left = Parse(a);
        var right = Parse(b);
        if (left is null && right is null) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        return left.CompareTo(right);
    }

    /// <summary>规范化显示：去掉 v 前缀与 +commit 后缀，保留预发布标记。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        return value;
    }
}

/// <summary>解析后的版本号（数字段 + 预发布标记）。</summary>
public sealed record ParsedVersion(int Major, int Minor, int Build, string Prerelease) : IComparable<ParsedVersion>
{
    public int CompareTo(ParsedVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Build.CompareTo(other.Build);
        if (result != 0) return result;
        // 数字段相同：无预发布标记的版本更新（1.0.0 > 1.0.0-beta）
        var hasLeft = !string.IsNullOrWhiteSpace(Prerelease);
        var hasRight = !string.IsNullOrWhiteSpace(other.Prerelease);
        if (hasLeft && hasRight) return string.CompareOrdinal(Prerelease, other.Prerelease);
        if (hasLeft) return -1;
        if (hasRight) return 1;
        return 0;
    }
}
