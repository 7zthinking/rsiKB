using System.Security.Cryptography;
using System.Text;
using KBEA.Core.Team;

namespace KBEA.Tests;

/// <summary>团队共享领域层测试：身份/团队存储/邀请码/导出/清单。</summary>
public class TeamShareTests : IDisposable
{
    private readonly string _root;

    public TeamShareTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"kbea-team-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static MachineIdentity CreateIdentity(string dir) =>
        MachineIdentity.LoadOrCreate(Path.Combine(dir, "profile.json"), nickname: "测试机");

    [Fact]
    public void Identity_SignAndVerify_RoundTrip()
    {
        var identity = CreateIdentity(_root);
        var signature = identity.Sign("hello|kbea");

        Assert.True(MachineIdentity.Verify(identity.PublicKey, "hello|kbea", signature));
        Assert.False(MachineIdentity.Verify(identity.PublicKey, "hello|kbea2", signature));
        Assert.False(MachineIdentity.Verify("invalid-key", "hello|kbea", signature));
    }

    [Fact]
    public void Identity_Reload_KeepsSameMachineCodeAndKey()
    {
        var path = Path.Combine(_root, "profile2.json");
        var first = MachineIdentity.LoadOrCreate(path, nickname: "甲");
        var second = MachineIdentity.LoadOrCreate(path, nickname: "乙");

        Assert.Equal(first.MachineCode, second.MachineCode);
        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal("乙", second.Nickname);
        Assert.Matches("^RSI-", first.DisplayCode);
    }

    [Fact]
    public void MachineCode_Is32Hex()
    {
        var identity = CreateIdentity(_root);
        Assert.Equal(32, identity.MachineCode.Length);
        Assert.All(identity.MachineCode, c => Assert.True(
            char.IsAsciiHexDigit(c)));
    }

    [Fact]
    public void TeamStore_CreateTeam_WritesSkeleton()
    {
        var store = new TeamStore(_root);
        var identity = CreateIdentity(_root);
        var team = store.CreateTeam(identity, "研发团队");

        Assert.True(Directory.Exists(Path.Combine(_root, team.Definition.TeamId)));
        Assert.Equal("owner", team.Members.Single().Role);
        Assert.Equal(identity.MachineCode, team.Definition.OwnerMachineCode);
        Assert.Empty(team.MyShares);
    }

    [Fact]
    public void InviteCode_GenerateAndParse_RoundTrip()
    {
        var store = new TeamStore(_root);
        var identity = CreateIdentity(_root);
        var team = store.CreateTeam(identity, "迁移团队");

        var shortCode = store.GenerateInviteCode(team.Definition, identity);

        // 短码形态：KBEA-XXXX-XXXX（8 位去易混字符）
        Assert.Matches(@"^KBEA-[A-Z2-9]{4}-[A-Z2-9]{4}$", shortCode);

        // 短码 → 解析出完整签名载荷
        var parsed = store.ResolveShortInvite(shortCode);
        Assert.NotNull(parsed);
        Assert.Equal(team.Definition.TeamId, parsed!.TeamId);
        Assert.Equal(team.Definition.Name, parsed.TeamName);
        Assert.Equal(identity.MachineCode, parsed.OwnerMachineCode);
        Assert.Equal(identity.PublicKey, parsed.OwnerPublicKey);

        // 错误短码 → null
        Assert.Null(store.ResolveShortInvite("KBEA-ZZZZ-ZZZZ"));

        // 重新生成 → 覆盖旧邀请，旧码立即失效
        var newCode = store.GenerateInviteCode(team.Definition, identity);
        Assert.NotEqual(shortCode, newCode);
        Assert.Null(store.ResolveShortInvite(shortCode));
        Assert.NotNull(store.ResolveShortInvite(newCode));
    }

    [Fact]
    public void InviteCode_TamperedPayload_Rejected()
    {
        var store = new TeamStore(_root);
        var identity = CreateIdentity(_root);
        var team = store.CreateTeam(identity, "防篡改");
        var code = store.GenerateInviteCode(team.Definition, identity);

        // 直接篡改磁盘上的 invite.json：换团队 ID（无 owner 私钥无法重签）
        var invitePath = store.InvitePath(team.Definition.TeamId);
        var json = File.ReadAllText(invitePath);
        Assert.Contains(team.Definition.TeamId, json);
        File.WriteAllText(invitePath, json.Replace(team.Definition.TeamId, "deadbeef0000"));

        Assert.Null(store.ResolveShortInvite(code));

        // 自包含 base64 长码的篡改同样被验签拒绝
        var payload = new InvitePayload
        {
            TeamId = team.Definition.TeamId,
            TeamName = team.Definition.Name,
            OwnerMachineCode = identity.MachineCode,
            OwnerNickname = identity.Nickname,
            OwnerPublicKey = identity.PublicKey,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Signature = "forged"
        };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Null(store.ParseInviteCode(encoded));
    }

    [Fact]
    public void TeamStore_JoinFlow_SavesMemberCopy()
    {
        var store = new TeamStore(_root);
        var owner = CreateIdentity(_root);
        var team = store.CreateTeam(owner, "加入测试");

        var joiner = new MemberRecord
        {
            MachineCode = "abc123",
            Nickname = "同事机",
            PublicKey = "<ECDsa/>",
            Role = "member"
        };
        team.Members.Add(joiner);
        store.WriteMembers(team.Definition.TeamId, team.Members);
        store.SaveJoinedTeam(team.Definition, team.Members, joiner.MachineCode);

        var reloaded = store.LoadTeam(team.Definition.TeamId, owner.MachineCode);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Members.Count);
        Assert.True(reloaded.Members.Any(m => m.MachineCode == "abc123"));
    }

    [Fact]
    public void TeamStore_MyShares_WriteAndRead()
    {
        var store = new TeamStore(_root);
        var identity = CreateIdentity(_root);
        var team = store.CreateTeam(identity, "共享测试");
        var spaceId = Guid.NewGuid();

        store.WriteMyShares(team.Definition.TeamId, identity.MachineCode, new List<SpaceShareDeclaration>
        {
            new() { SpaceId = spaceId, SpaceName = "设计文档", TeamId = team.Definition.TeamId, Level = ShareLevel.Write }
        });

        var shares = store.ReadShares(team.Definition.TeamId, identity.MachineCode);
        var share = Assert.Single(shares);
        Assert.Equal(spaceId, share.SpaceId);
        Assert.Equal("设计文档", share.SpaceName);
        Assert.Equal(ShareLevel.Write, share.Level);
    }

    [Fact]
    public void MetadataExporter_ExportsNdjsonWithHashes()
    {
        var spaceDir = Path.Combine(_root, "space");
        Directory.CreateDirectory(Path.Combine(spaceDir, "docs"));
        File.WriteAllText(Path.Combine(spaceDir, "readme.md"), "# 你好");
        File.WriteAllBytes(Path.Combine(spaceDir, "docs", "a.bin"), RandomNumberGenerator.GetBytes(2048));

        var outDir = Path.Combine(_root, "export");
        var exporter = new MetadataExporter();
        var count = exporter.Export(Guid.NewGuid(), "设计文档", spaceDir, outDir, "team1");

        Assert.Equal(2, count);
        var ndjson = Path.Combine(outDir, $"{Guid.Empty:N}");
        // 目录名不是 Guid.Empty；直接扫描导出文件
        var files = Directory.GetFiles(outDir, "*.ndjson");
        var target = Assert.Single(files);
        var lines = File.ReadAllLines(target);
        Assert.Equal(2, lines.Length);
        var first = System.Text.Json.JsonSerializer.Deserialize<MetadataEntry>(lines[0]);
        Assert.NotNull(first);
        Assert.Equal(64, first!.Hash.Length); // 小文件 → 内容 SHA-256 十六进制（64 字符）
    }

    [Fact]
    public void MetadataExporter_Incremental_ReusesHashes()
    {
        var spaceDir = Path.Combine(_root, "space2");
        Directory.CreateDirectory(spaceDir);
        File.WriteAllText(Path.Combine(spaceDir, "a.txt"), "内容不变");
        var outDir = Path.Combine(_root, "export2");
        var exporter = new MetadataExporter();
        exporter.Export(Guid.NewGuid(), "s", spaceDir, outDir);
        var target = Directory.GetFiles(outDir, "*.ndjson").Single();
        var before = File.ReadAllText(target);

        // 内容没变（mtime 也没变）→ 缓存命中，导出结果一致
        var count = exporter.Export(Guid.NewGuid(), "s", spaceDir, outDir);
        Assert.Equal(1, count);
        Assert.Equal(before, File.ReadAllText(target));
    }

    // ---------- WebDAV 访问令牌（Basic 认证密码） ----------

    [Fact]
    public void WebDavToken_CreateAndVerify_RoundTrip()
    {
        var identity = CreateIdentity(_root);
        var token = identity.CreateWebDavToken();

        Assert.Matches($"^{identity.MachineCode}:", token);
        Assert.True(MachineIdentity.VerifyWebDavToken(token, identity.PublicKey, identity.MachineCode));
    }

    [Fact]
    public void WebDavToken_Expired_Rejected()
    {
        var identity = CreateIdentity(_root);
        var token = identity.CreateWebDavToken(hours: 0);

        Assert.False(MachineIdentity.VerifyWebDavToken(token, identity.PublicKey, identity.MachineCode));
    }

    [Fact]
    public void WebDavToken_WrongKeyOrCode_Rejected()
    {
        var identity = CreateIdentity(_root);
        var other = MachineIdentity.LoadOrCreate(Path.Combine(_root, "profile-other.json"));
        var token = identity.CreateWebDavToken();

        // 别的成员公钥验签 → 失败；机器码不匹配 → 失败；格式损坏 → 失败
        // （同一物理机上两个身份的机器码相同，机器码不匹配用伪造码断言）
        Assert.False(MachineIdentity.VerifyWebDavToken(token, other.PublicKey, identity.MachineCode));
        Assert.False(MachineIdentity.VerifyWebDavToken(token, identity.PublicKey, new string('0', 32)));
        Assert.False(MachineIdentity.VerifyWebDavToken("garbage", identity.PublicKey, identity.MachineCode));
    }
}
