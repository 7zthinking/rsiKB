using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KBEA.Core.Team;

/// <summary>
/// 团队存储：团队 = %APPDATA%\KBEA\teams\&lt;teamId&gt;\ 文件夹的全部状态（复制即备份/迁移）。
///   team.json            团队定义（本地记录）
///   members.jsonl        成员表（owner 独占写，成员保存 owner 广播的副本）
///   sync\&lt;机器码&gt;\       每成员独立子目录（只有本机写自己的）
///     manifest.json      版本戳清单
///     spaces.jsonl       该成员的共享空间声明
///     metadata\*.ndjson  导出的空间元数据
/// </summary>
public sealed class TeamStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new() { WriteIndented = false };

    public string RootPath { get; }

    public TeamStore(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            MachineIdentity.DataDirOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KBEA", "teams");
    }

    // ---------- 路径 ----------

    public string TeamDir(string teamId) => Path.Combine(RootPath, teamId);
    public string MembersPath(string teamId) => Path.Combine(TeamDir(teamId), "members.jsonl");
    public string TeamJsonPath(string teamId) => Path.Combine(TeamDir(teamId), "team.json");
    public string SyncDir(string teamId, string machineCode) => Path.Combine(TeamDir(teamId), "sync", machineCode);
    public string ManifestPath(string teamId, string machineCode) => Path.Combine(SyncDir(teamId, machineCode), "manifest.json");
    public string SharesPath(string teamId, string machineCode) => Path.Combine(SyncDir(teamId, machineCode), "spaces.jsonl");

    // ---------- 团队生命周期 ----------

    /// <summary>本机已加入的全部团队（读取本地副本）。myMachineCode 用于定位自己的 sync 子目录。</summary>
    public List<TeamFolder> LoadTeams(string myMachineCode)
    {
        var result = new List<TeamFolder>();
        if (!Directory.Exists(RootPath)) return result;
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            var teamId = Path.GetFileName(dir);
            var team = ReadTeamJson(teamId);
            if (team is null) continue;
            result.Add(new TeamFolder(team, ReadMembers(teamId), ReadShares(teamId, myMachineCode)));
        }
        return result;
    }

    public TeamFolder? LoadTeam(string teamId, string myMachineCode) => ReadTeamJson(teamId) is { } definition
        ? new TeamFolder(definition, ReadMembers(teamId), ReadShares(teamId, myMachineCode))
        : null;

    /// <summary>新建团队：本机成为 owner。</summary>
    public TeamFolder CreateTeam(MachineIdentity identity, string name)
    {
        var teamId = NewTeamId();
        var definition = new TeamDefinition
        {
            TeamId = teamId,
            Name = name,
            OwnerMachineCode = identity.MachineCode,
            CreatedAt = DateTimeOffset.UtcNow
        };
        Directory.CreateDirectory(SyncDir(teamId, identity.MachineCode));
        Directory.CreateDirectory(Path.Combine(SyncDir(teamId, identity.MachineCode), "metadata"));
        WriteJson(TeamJsonPath(teamId), definition);
        WriteJsonl(MembersPath(teamId), new List<MemberRecord> { OwnerMember(identity) });
        WriteJsonl(SharesPath(teamId, identity.MachineCode), new List<SpaceShareDeclaration>());
        return new TeamFolder(definition, ReadMembers(teamId), ReadShares(teamId, identity.MachineCode));
    }

    /// <summary>保存加入成功的团队（成员端：team.json + owner 下发的成员表副本）。</summary>
    public void SaveJoinedTeam(TeamDefinition definition, List<MemberRecord> members, string myMachineCode)
    {
        Directory.CreateDirectory(SyncDir(definition.TeamId, myMachineCode));
        WriteJson(TeamJsonPath(definition.TeamId), definition);
        WriteJsonl(MembersPath(definition.TeamId), members);
        if (!File.Exists(SharesPath(definition.TeamId, myMachineCode)))
            WriteJsonl(SharesPath(definition.TeamId, myMachineCode), new List<SpaceShareDeclaration>());
    }

    /// <summary>owner 写入成员表（接受新成员 / 移除成员）。</summary>
    public List<MemberRecord> WriteMembers(string teamId, List<MemberRecord> members)
    {
        WriteJsonl(MembersPath(teamId), members);
        return members;
    }

    /// <summary>退出团队：删除本地团队文件夹。</summary>
    public void LeaveTeam(string teamId)
    {
        var dir = TeamDir(teamId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // ---------- 成员表 ----------

    public List<MemberRecord> ReadMembers(string teamId)
    {
        var list = new List<MemberRecord>();
        foreach (var line in ReadLines(MembersPath(teamId)))
        {
            try
            {
                var member = JsonSerializer.Deserialize<MemberRecord>(line, JsonLineOptions);
                if (member is not null) list.Add(member);
            }
            catch (JsonException) { /* 跳过损坏行 */ }
        }
        return list;
    }

    private static MemberRecord OwnerMember(MachineIdentity identity) => new()
    {
        MachineCode = identity.MachineCode,
        Nickname = identity.Nickname,
        PublicKey = identity.PublicKey,
        Role = "owner",
        JoinedAt = DateTimeOffset.UtcNow
    };

    // ---------- 共享声明 ----------

    public List<SpaceShareDeclaration> ReadShares(string teamId, string machineCode)
    {
        var list = new List<SpaceShareDeclaration>();
        foreach (var line in ReadLines(SharesPath(teamId, machineCode)))
        {
            try
            {
                var share = JsonSerializer.Deserialize<SpaceShareDeclaration>(line, JsonLineOptions);
                if (share is not null) list.Add(share);
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>写本机对某团队的共享声明（写入自己 sync 子目录，随 manifest 同步给团队）。</summary>
    public void WriteMyShares(string teamId, string myMachineCode, List<SpaceShareDeclaration> shares)
        => WriteJsonl(SharesPath(teamId, myMachineCode), shares);

    // ---------- 邀请码 ----------

    /// <summary>owner 生成团队邀请码（含团队信息 + owner 签名，默认 7 天有效）。</summary>
    public string GenerateInviteCode(TeamDefinition team, MachineIdentity identity, int validDays = 7)
    {
        var payload = new InvitePayload
        {
            TeamId = team.TeamId,
            TeamName = team.Name,
            OwnerMachineCode = team.OwnerMachineCode,
            OwnerNickname = identity.Nickname,
            OwnerPublicKey = identity.PublicKey,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(validDays),
            Signature = string.Empty
        };
        SignInvitePayload(payload, identity);
        var shortCode = GenerateShortInvite(team, payload, validDays);
        return shortCode ?? Base64UrlEncode(JsonSerializer.Serialize(payload, JsonLineOptions));
    }

    /// <summary>给邀请载荷签名（防伪造：joiner 用码内公钥验签）。</summary>
    public static void SignInvitePayload(InvitePayload payload, MachineIdentity identity)
    {
        payload.Signature = identity.Sign(InviteSignText(payload));
    }

    private static string InviteSignText(InvitePayload payload)
        => $"invite|{payload.TeamId}|{payload.OwnerMachineCode}|{payload.ExpiresAt.ToUnixTimeSeconds()}";

    /// <summary>校验邀请载荷的 owner 签名与有效期。</summary>
    public static bool ValidateInvitePayload(InvitePayload payload)
    {
        if (payload is null || string.IsNullOrEmpty(payload.Signature) || string.IsNullOrEmpty(payload.OwnerPublicKey)) return false;
        if (payload.ExpiresAt < DateTimeOffset.UtcNow) return false;
        return MachineIdentity.Verify(payload.OwnerPublicKey, InviteSignText(payload), payload.Signature);
    }

    // ---------- 短码邀请 ----------

    private sealed record ShortInvite(string Code, DateTimeOffset ExpiresAt, InvitePayload Payload);

    /// <summary>短码路径（团队目录下 invite.json，一码一份，重新生成即覆盖=旧码失效）。</summary>
    public string InvitePath(string teamId) => Path.Combine(TeamDir(teamId), "invite.json");

    /// <summary>
    /// 生成 8 位短码（KBEA-XXXX-XXXX，去掉易混字符 0O1I），落盘 invite.json 并返回短码。
    /// 短码不自带公钥/签名——加入方通过 mDNS 找到 owner，用 HTTP 换取完整签名载荷后验签，信任链不降级。
    /// </summary>
    public string? GenerateShortInvite(TeamDefinition team, InvitePayload payload, int validDays = 7)
    {
        try
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var random = Random.Shared;
            var code = new string(Enumerable.Range(0, 8).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var formatted = $"KBEA-{code[..4]}-{code[4..]}";
            TeamStore.WriteJson(InvitePath(team.TeamId), new ShortInvite(formatted, payload.ExpiresAt, payload));
            return formatted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>校验短码：匹配 invite.json 且未过期 → 返回完整签名载荷。</summary>
    public InvitePayload? ResolveShortInvite(string code)
    {
        try
        {
            if (!Directory.Exists(RootPath)) return null;
            var normalized = code.Trim().ToUpperInvariant();
            foreach (var inviteFile in Directory.EnumerateFiles(RootPath, "invite.json", SearchOption.AllDirectories))
            {
                try
                {
                    var invite = JsonSerializer.Deserialize<ShortInvite>(File.ReadAllText(inviteFile), JsonLineOptions);
                    if (invite is null || !string.Equals(invite.Code.Trim(), normalized, StringComparison.OrdinalIgnoreCase)) continue;
                    if (invite.ExpiresAt < DateTimeOffset.UtcNow) continue;
                    if (!ValidateInvitePayload(invite.Payload)) continue;
                    return invite.Payload;
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>解析并校验邀请码（用码内 owner 公钥验签 + 过期检查）。</summary>
    public InvitePayload? ParseInviteCode(string code)
    {
        try
        {
            var json = Base64UrlDecode(code);
            var payload = JsonSerializer.Deserialize<InvitePayload>(json, JsonLineOptions);
            if (payload is null) return null;
            if (!ValidateInvitePayload(payload)) return null;
            return payload;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    // ---------- 元数据读取（他人 sync 子目录，用于搜索与浏览聚合） ----------

    /// <summary>读取某成员导出的全部元数据行。</summary>
    public List<MetadataEntry> ReadPeerMetadata(string teamId, string peerMachineCode, string teamName, string peerNickname)
    {
        var result = new List<MetadataEntry>();
        var dir = Path.Combine(SyncDir(teamId, peerMachineCode), "metadata");
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.ndjson"))
        {
            foreach (var line in ReadLines(file))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<MetadataEntry>(line, JsonLineOptions);
                    if (entry is null) continue;
                    entry.TeamId ??= teamId;
                    if (string.IsNullOrEmpty(entry.TeamId)) entry.TeamId = teamId;
                    if (string.IsNullOrEmpty(entry.TeamName)) entry.TeamName = teamName;
                    if (string.IsNullOrEmpty(entry.SpaceName)) entry.SpaceName = Path.GetFileNameWithoutExtension(file);
                    result.Add(entry);
                }
                catch (JsonException) { }
            }
        }
        return result;
    }

    // ---------- 基础工具 ----------

    private TeamDefinition? ReadTeamJson(string teamId)
    {
        var path = TeamJsonPath(teamId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<TeamDefinition>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line));
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>原子写 JSON（tmp + rename，防 manifest 竞态读到半截文件）。</summary>
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>原子写 JSONL。</summary>
    public static void WriteJsonl<T>(string path, IEnumerable<T> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        foreach (var value in values)
            sb.AppendLine(JsonSerializer.Serialize(value, JsonLineOptions));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, path, overwrite: true);
    }

    public static string NewTeamId()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Base64UrlEncode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Base64UrlDecode(string code)
    {
        var base64 = code.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}

/// <summary>本地团队文件夹视图：定义 + 成员表副本 + 本机共享声明。</summary>
public sealed class TeamFolder
{
    public TeamFolder(TeamDefinition definition, List<MemberRecord> members, List<SpaceShareDeclaration> myShares)
    {
        Definition = definition;
        Members = members;
        MyShares = myShares;
    }

    public TeamDefinition Definition { get; }
    public List<MemberRecord> Members { get; set; }
    public List<SpaceShareDeclaration> MyShares { get; set; }
    public bool IsOwner(string machineCode) => Definition.OwnerMachineCode == machineCode;
}
