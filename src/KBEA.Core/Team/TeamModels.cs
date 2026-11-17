using System.Text.Json.Serialization;

namespace KBEA.Core.Team;

/// <summary>空间共享级别：search = 只进索引；read = 可取回文件；write = 可写回。</summary>
public enum ShareLevel { Search = 0, Read = 1, Write = 2 }

/// <summary>团队成员记录（members.jsonl 每行一条，owner 独占写）。</summary>
public sealed class MemberRecord
{
    public string MachineCode { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    /// <summary>签名公钥（ECDsa P-256，XML 序列化）。</summary>
    public string PublicKey { get; set; } = string.Empty;
    public string Role { get; set; } = "member";
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsOwner => Role == "owner";
}

/// <summary>团队定义（teams/&lt;teamId&gt;/team.json）。</summary>
public sealed class TeamDefinition
{
    public string TeamId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OwnerMachineCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>本机声明：把哪个空间共享给哪个团队、什么级别（写入自己 sync 子目录的 spaces.jsonl）。</summary>
public sealed class SpaceShareDeclaration
{
    public Guid SpaceId { get; set; }
    public string SpaceName { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public ShareLevel Level { get; set; } = ShareLevel.Read;
}

/// <summary>manifest.json：版本戳清单（version 单调递增，不同即更新）。</summary>
public sealed class TeamManifest
{
    public long Version { get; set; }
    public string MachineCode { get; set; } = string.Empty;
    public List<ManifestEntry> Files { get; set; } = [];
}

public sealed record ManifestEntry(string Path, string Hash, long Size);

/// <summary>元数据 NDJSON 行：共享空间的文件条目（不含文件本体）。</summary>
public sealed class MetadataEntry
{
    public string TeamId { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public Guid SpaceId { get; set; }
    public string SpaceName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Size { get; set; }
    /// <summary>Unix 秒。</summary>
    public long Mtime { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Description { get; set; } = string.Empty;

    public bool IsDirectory => Path.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(Path);
}

/// <summary>邀请码载荷（owner 签名后 base64url 编码为团队码）。</summary>
public sealed class InvitePayload
{
    public string TeamId { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public string OwnerMachineCode { get; set; } = string.Empty;
    public string OwnerNickname { get; set; } = string.Empty;
    /// <summary>owner 公钥（joiner 首次接触 owner，靠码内公钥验签）。</summary>
    public string OwnerPublicKey { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string Signature { get; set; } = string.Empty;
}

/// <summary>加入团队请求（joiner → owner HTTP POST /t/teams/join）。</summary>
public sealed class JoinRequest
{
    public InvitePayload Invite { get; set; } = new();
    public string MachineCode { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
}

/// <summary>加入团队响应：owner 返回完整成员表。</summary>
public sealed class JoinResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public TeamDefinition? Team { get; set; }
    public List<MemberRecord> Members { get; set; } = [];
}

/// <summary>mDNS 上发现的局域网同伴（另一台 KBEA 实例）。</summary>
public sealed class PeerInfo
{
    public string MachineCode { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public int Port { get; set; }
    /// <summary>对端 HTTPS 端口（0 = 对端旧版本未广播，走 HTTP）。</summary>
    public int HttpsPort { get; set; }
    public string Ip { get; set; } = string.Empty;
    public List<string> Teams { get; set; } = [];
    public DateTimeOffset LastSeen { get; set; }
    public bool Online { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? MachineCode : Nickname;
    /// <summary>请求基址：对端广播了 HTTPS 端口则优先 https（公钥钉扎），否则回退 http。</summary>
    public string BaseUrl => string.IsNullOrEmpty(Ip)
        ? string.Empty
        : HttpsPort > 0 ? $"https://{Ip}:{HttpsPort}" : $"http://{Ip}:{Port}";
}

/// <summary>搜索/浏览用的共享条目（聚合视图：谁的 + 哪个空间）。</summary>
public sealed class SharedEntry
{
    public string TeamId { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public string OwnerMachineCode { get; set; } = string.Empty;
    public string OwnerNickname { get; set; } = string.Empty;
    public Guid SpaceId { get; set; }
    public string SpaceName { get; set; } = string.Empty;
    public ShareLevel Level { get; set; } = ShareLevel.Read;
    public bool Online { get; set; }

    public string LevelText => Level switch
    {
        ShareLevel.Search => "可搜索",
        ShareLevel.Read => "可读取",
        ShareLevel.Write => "可编辑",
        _ => string.Empty
    };
    public string Display => $"{OwnerNickname} · {SpaceName}";
}
