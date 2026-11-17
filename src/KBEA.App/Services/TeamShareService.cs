using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using KBEA.Core.Models;
using KBEA.Core.Team;

namespace KBEA.App.Services;

/// <summary>
/// 团队共享总编排：身份 + mDNS 发现 + Kestrel 服务 + 同步引擎 + 引用空间镜像。
/// 由 MainViewModel 持有并在启动时 Start。
/// </summary>
public sealed class TeamShareService : IDisposable
{
    public const int PreferredPort = 52235;

    private readonly TeamStore _store;
    private readonly MachineIdentity _identity;
    private readonly TeamClient _client;
    private TeamShareServer _server = null!;
    private readonly DiscoveryService _discovery = null!;
    private readonly TeamSyncEngine _syncEngine = null!;
    private ContentCache _cache = null!;
    private readonly System.Timers.Timer _syncTimer = null!;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Action<string> _log;

    /// <summary>SpaceId → 本机空间路径（MainViewModel 提供配置视图）。</summary>
    private readonly Func<Guid, SpaceDefinition?> _spaceResolver;
    private readonly Func<TeamSettings> _settings;
    private readonly TeamSpaceMirror _mirror;

    private CancellationTokenSource? _pullCts;

    /// <summary>共享设置（设置页可改，启动时读取）：缓存限额 GB / 同步间隔分钟 / 服务端口。</summary>
    public sealed record TeamSettings(double CacheLimitGb, int SyncIntervalMinutes, int ServicePort);

    public TeamShareService(Func<Guid, SpaceDefinition?> spaceResolver, Func<TeamSettings> settings, Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        _spaceResolver = spaceResolver;
        _settings = settings;
        _store = new TeamStore();
        _identity = MachineIdentity.LoadOrCreate();
        _client = new TeamClient(_identity);
        _mirror = new TeamSpaceMirror(_client, GetPeer);
        _server = new TeamShareServer(_store, _identity, () => Teams, id => _spaceResolver(id));
        _discovery = new DiscoveryService(_identity, () => Teams.Select(t => t.Definition.TeamId).ToList());
        _syncEngine = new TeamSyncEngine(_store, _identity, GetPeer, _log);
        _cache = new ContentCache();
        _syncTimer = new System.Timers.Timer(TimeSpan.FromMinutes(5)) { AutoReset = true };
        _syncTimer.Elapsed += (_, _) => _ = SyncNowAsync("定时");

        _discovery.PeersChanged += () => PeersChanged?.Invoke();
    }

    // ---------- 状态 ----------

    /// <summary>当前全部团队（每次变更后重新从盘加载）。</summary>
    public List<TeamFolder> Teams { get; private set; } = [];

    public MachineIdentity Identity => _identity;
    public TeamStore Store => _store;
    public TeamSpaceMirror Mirror => _mirror;
    public ContentCache Cache => _cache;
    public int ServerPort => _server.Port;
    public bool IsRunning { get; private set; }

    /// <summary>同伴在线状态变化（mDNS 线程触发，UI 需自行调度）。</summary>
    public event Action? PeersChanged;

    /// <summary>一轮同步完成（共享条目可能变化）。</summary>
    public event Action? SharesChanged;

    public PeerInfo? GetPeer(string machineCode) => _discovery.GetPeer(machineCode);

    public List<PeerInfo> Peers => _discovery.SnapshotPeers();

    // ---------- 生命周期 ----------

    public void Start()
    {
        if (IsRunning) return;
        var settings = _settings();
        var cacheLimitBytes = (long)Math.Clamp(settings.CacheLimitGb, 0.1, 100) * 1024 * 1024 * 1024;
        _cache = new ContentCache(cacheLimitBytes);
        _syncTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(settings.SyncIntervalMinutes, 1, 120)).TotalMilliseconds;
        ReloadTeams();
        var port = _server.Start(settings.ServicePort > 0 ? settings.ServicePort : PreferredPort);
        if (port == 0)
        {
            _log("[团队] 服务端口启动失败（连续 6 个端口被占用）");
            return;
        }
        _discovery.ServicePort = port;
        _discovery.ServiceHttpsPort = _server.HttpsPort;
        try
        {
            _discovery.Start();
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _log($"[团队] mDNS 启动失败：{ex.Message}（可改用手动添加成员）");
        }
        IsRunning = true;
        _syncTimer.Start();
        PushWebDavCredentials();
        _ = Task.Run(() => SyncNowAsync("启动"));
        _log($"[团队] 本机身份 {_identity.DisplayCode} · 服务端口 {port}" +
             (_server.HttpsPort > 0 ? $"（HTTPS {_server.HttpsPort}）" : "") + $" · 昵称 {_identity.Nickname}");
    }

    public void Dispose()
    {
        _syncTimer.Stop();
        _pullCts?.Cancel();
        _discovery.Dispose();
        _server.Dispose();
        _syncGate.Dispose();
    }

    // ---------- 团队操作 ----------

    public void ReloadTeams()
    {
        Teams = _store.LoadTeams(_identity.MachineCode);
        // 重建信任公钥库：全部成员（含 owner 与本机）→ HTTPS 客户端按公钥钉扎自签证书
        TeamTrustStore.Clear();
        TeamTrustStore.Add(_identity.PublicKey);
        foreach (var team in Teams)
            foreach (var member in team.Members)
                TeamTrustStore.Add(member.PublicKey);
    }

    /// <summary>新建团队（本机成为 owner），返回团队。</summary>
    public TeamFolder CreateTeam(string name)
    {
        var team = _store.CreateTeam(_identity, name);
        ReloadTeams();
        RefreshAdvertisement();
        return team;
    }

    /// <summary>生成当前团队的邀请码（owner）。</summary>
    public string GenerateInviteCode(TeamFolder team) => _store.GenerateInviteCode(team.Definition, _identity);

    /// <summary>owner 接受加入请求后广播成员表已由服务端完成；这里刷新团队列表与广播。</summary>
    public void NotifyMembersChanged()
    {
        ReloadTeams();
        RefreshAdvertisement();
    }

    /// <summary>用邀请码加入团队：短码经 mDNS+HTTP 向 owner 换取完整签名载荷（本机 invite.json 也会兜底解析）；owner 在线即自动入队，离线报错。</summary>
    public async Task<(bool Success, string Message)> JoinTeamAsync(string inviteCode)
    {
        var invite = await ResolveInvite(inviteCode.Trim()).ConfigureAwait(false);
        if (invite is null) return (false, "邀请码无效或已过期（owner 不在线时短码无法换取，请稍后再试）");
        if (invite.OwnerMachineCode == _identity.MachineCode) return (false, "不能加入自己的团队");
        var owner = GetPeer(invite.OwnerMachineCode);
        if (owner is not { Online: true }) return (false, $"团队 owner（{invite.OwnerNickname}）当前不在线，稍后再试");

        var response = await _client.JoinTeamAsync(owner, invite).ConfigureAwait(false);
        if (response is not { Success: true } || response.Team is null)
            return (false, response?.Message is { Length: > 0 } message ? message : "加入失败");
        _store.SaveJoinedTeam(response.Team, response.Members, _identity.MachineCode);
        ReloadTeams();
        RefreshAdvertisement();
        _ = Task.Run(() => SyncNowAsync("加入团队"));
        return (true, $"已加入团队「{response.Team.Name}」");
    }

    /// <summary>退出团队。</summary>
    public void LeaveTeam(string teamId)
    {
        _store.LeaveTeam(teamId);
        ReloadTeams();
        RefreshAdvertisement();
    }

    /// <summary>邀请码解析：短码（KBEA-XXXX-XXXX）经 mDNS 找在线机器逐个 HTTP 换取完整载荷，本机 invite.json 兜底；长 base64 码直接本地解析。</summary>
    private async Task<InvitePayload?> ResolveInvite(string code)
    {
        // 长码（自包含 base64）→ 本地验签解析
        if (code.Length > 30)
            return _store.ParseInviteCode(code);

        // 短码 → 先问本机（owner 自己重join或同机测试），再遍历在线 peer
        var local = _store.ResolveShortInvite(code);
        if (local is not null) return local;

        foreach (var peer in Peers.Where(p => p.Online))
        {
            var payload = await _client.GetShortInviteAsync(peer, code).ConfigureAwait(false);
            if (payload is not null && TeamStore.ValidateInvitePayload(payload)) return payload;
        }
        return null;
    }

    /// <summary>更新本机对某团队的共享声明（TeamLink 空间禁止共享，调用方保证）。</summary>
    public void SetShares(string teamId, List<SpaceShareDeclaration> shares)
    {
        _store.WriteMyShares(teamId, _identity.MachineCode, shares);
        ReloadTeams();
        RefreshAdvertisement();
        // 元数据导出（逐文件算 hash）必须在后台跑：SyncNowAsync 内部会先 ExportOwn 再拉取。
        // 之前在这里同步导出，大空间会冻结 UI 线程数分钟（点添加共享空间即卡死的根因）
        _ = Task.Run(() => SyncNowAsync("共享变更"));
    }

    private void RefreshAdvertisement()
    {
        try { _discovery.RefreshAdvertisement(); }
        catch (Exception ex) when (ex is SocketException or IOException) { }
    }

    // ---------- WebDAV 自动凭据 ----------

    /// <summary>
    /// 给每个在线 peer 写 Windows 凭据（cmdkey）：user=本机机器码、pass=本机签发的 24h WebDAV 令牌。
    /// 这样外部程序（资源管理器映射 UNC）无需手动输密码即可通过服务端 Basic 认证。
    /// WebClient 凭据目标格式在「ip:port」与「ip@port」间因系统版本而异，双写幂等覆盖；
    /// 每轮同步重写即滚动续期。失败静默（用户仍可手动映射输密码）。
    /// </summary>
    private void PushWebDavCredentials()
    {
        var token = _identity.CreateWebDavToken();
        foreach (var peer in Peers.Where(p => p.Online))
        {
            PushCredential($"{peer.Ip}:{peer.Port}", _identity.MachineCode, token);
            if (peer.HttpsPort > 0) PushCredential($"{peer.Ip}:{peer.HttpsPort}", _identity.MachineCode, token);
        }
    }

    private static void PushCredential(string target, string machineCode, string token)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c cmdkey /add:\"{target}\" /user:{machineCode} /pass:{token}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { }
    }

    // ---------- 同步 ----------

    /// <summary>一轮完整同步：导出自己 + 拉取他人。可并发调用（信号量防重入）。</summary>
    public async Task SyncNowAsync(string trigger)
    {
        if (!IsRunning) return;
        if (!await _syncGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            _pullCts = new CancellationTokenSource();
            foreach (var team in Teams)
            {
                _syncEngine.ExportOwn(team, id => _spaceResolver(id)?.Path);
            }
            await _syncEngine.PullAllAsync(Teams, _pullCts.Token).ConfigureAwait(false);
            SharesChanged?.Invoke();
            // 同步成功后刷新一次凭据：此时在线 peer 可能比启动时多（cmdkey 幂等覆盖 = 令牌滚动续期）
            PushWebDavCredentials();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[团队] 同步异常：{ex.Message}");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    // ---------- 共享条目聚合（UI / 搜索用） ----------

    /// <summary>团队里其他成员共享的全部空间（每成员 × 每空间一条）。</summary>
    public List<SharedEntry> GetSharedEntries()
    {
        var result = new List<SharedEntry>();
        foreach (var team in Teams)
        {
            foreach (var member in team.Members)
            {
                if (member.MachineCode == _identity.MachineCode) continue;
                var peer = GetPeer(member.MachineCode);
                foreach (var share in _store.ReadShares(team.Definition.TeamId, member.MachineCode))
                {
                    result.Add(new SharedEntry
                    {
                        TeamId = team.Definition.TeamId,
                        TeamName = team.Definition.Name,
                        OwnerMachineCode = member.MachineCode,
                        OwnerNickname = member.Nickname,
                        SpaceId = share.SpaceId,
                        SpaceName = share.SpaceName,
                        Level = share.Level,
                        Online = peer is { Online: true }
                    });
                }
            }
        }
        return result;
    }

    /// <summary>在已同步的团队元数据里检索（文件名/路径/描述），返回带来源的命中。</summary>
    public List<(MetadataEntry Entry, SharedEntry Source)> SearchShared(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<(MetadataEntry, SharedEntry)>();
        foreach (var team in Teams)
        {
            foreach (var member in team.Members)
            {
                if (member.MachineCode == _identity.MachineCode) continue;
                var peer = GetPeer(member.MachineCode);
                var online = peer is { Online: true };
                foreach (var share in _store.ReadShares(team.Definition.TeamId, member.MachineCode))
                {
                    var source = new SharedEntry
                    {
                        TeamId = team.Definition.TeamId,
                        TeamName = team.Definition.Name,
                        OwnerMachineCode = member.MachineCode,
                        OwnerNickname = member.Nickname,
                        SpaceId = share.SpaceId,
                        SpaceName = share.SpaceName,
                        Level = share.Level,
                        Online = online
                    };
                    foreach (var entry in _store.ReadPeerMetadata(team.Definition.TeamId, member.MachineCode, team.Definition.Name, member.Nickname))
                    {
                        if (entry.SpaceId != share.SpaceId) continue;
                        if (keywords.Any(k => entry.Path.Contains(k, StringComparison.OrdinalIgnoreCase)
                            || entry.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            result.Add((entry, source));
                        if (result.Count >= 200) return result;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>把共享条目添加为「我的工作空间」里的团队引用空间（TeamLink），并做首次镜像同步。</summary>
    public SpaceDefinition CreateTeamLinkSpace(SharedEntry entry)
    {
        var space = new SpaceDefinition
        {
            Name = $"{entry.OwnerNickname}·{entry.SpaceName}",
            Kind = SpaceKind.TeamLink,
            TeamId = entry.TeamId,
            OwnerMachineCode = entry.OwnerMachineCode,
            RemoteSpaceId = entry.SpaceId,
            Path = _mirror.MirrorRoot(entry.SpaceId)
        };
        Directory.CreateDirectory(space.Path);
        return space;
    }

    public Task<(int Synced, bool Online)> SyncTeamLinkAsync(SpaceDefinition space, CancellationToken token = default)
        => _mirror.SyncAsync(space, token);
}
