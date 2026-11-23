using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Makaretu.Dns;

namespace KBEA.Core.Team;

/// <summary>
/// mDNS 发现服务：广播本机（_rsikb._tcp.local.）+ 浏览局域网其他 KBEA 实例。
/// TXT 记录：mc=机器码、nm=昵称、teams=团队 ID 列表、ts=时间戳、sig=签名。
/// 在线状态 = mDNS 出现/消失事件 + 陈旧检测（90 秒未现身视为离线）。
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private const string ServiceName = "_rsikb._tcp";
    private const int StaleSeconds = 90;

    private readonly MachineIdentity _identity;
    private readonly Func<List<string>> _teamIdProvider;
    private readonly System.Timers.Timer _staleTimer;
    private readonly object _sync = new();

    private MulticastService? _mdns;
    private ServiceDiscovery? _sdns;
    private ServiceProfile? _profile;
    private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);
    private bool _running;

    public event Action? PeersChanged;

    /// <summary>局域网诊断快照：本机广播状态 + mDNS 应答统计 + 网卡信息（供诊断面板展示）。</summary>
    public sealed record LanDiagnostics(
        bool Running,
        int ServicePort,
        int HttpsPort,
        IReadOnlyList<string> AdvertiseIps,
        int AnswerTotal,
        int AnswerSelfIgnored,
        int AnswerNameMismatch,
        IReadOnlyList<string> Interfaces);

    /// <summary>mDNS 应答计数（含被过滤的），lock(_sync) 下更新。</summary>
    private int _answerTotal, _answerSelfIgnored, _answerNameMismatch;

    /// <summary>取诊断快照：广播 IP、应答统计、Up 状态网卡（IP+网关）。</summary>
    public LanDiagnostics GetDiagnostics()
    {
        lock (_sync)
        {
            var interfaces = new List<string>();
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    var ip = n.GetIPProperties().GatewayAddresses.Count > 0
                        ? string.Join(", ", n.GetIPProperties().GatewayAddresses.Select(g => g.Address))
                        : "无网关";
                    interfaces.Add($"{n.NetworkInterfaceType} | {ip} | {string.Join(", ", n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address))}");
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }

            var advertised = _profile is not null
                ? GetAdvertiseAddresses().Select(a => a.ToString()).ToList()
                : [];
            return new LanDiagnostics(_running, ServicePort, ServiceHttpsPort, advertised,
                _answerTotal, _answerSelfIgnored, _answerNameMismatch, interfaces);
        }
    }

    public DiscoveryService(MachineIdentity identity, Func<List<string>> teamIdProvider)
    {
        _identity = identity;
        _teamIdProvider = teamIdProvider;
        _staleTimer = new System.Timers.Timer(30_000) { AutoReset = true };
        _staleTimer.Elapsed += (_, _) => MarkStalePeers();
    }

    /// <summary>当前全部已知同伴（含离线）。</summary>
    public List<PeerInfo> SnapshotPeers()
    {
        lock (_sync) return _peers.Values.Select(p => new PeerInfo
        {
            MachineCode = p.MachineCode,
            Nickname = p.Nickname,
            Port = p.Port,
            HttpsPort = p.HttpsPort,
            Ip = p.Ip,
            Teams = [.. p.Teams],
            LastSeen = p.LastSeen,
            Online = p.Online
        }).ToList();
    }

    public PeerInfo? GetPeer(string machineCode)
    {
        lock (_sync) return _peers.TryGetValue(machineCode, out var peer) ? peer : null;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _mdns = new MulticastService();
        _mdns.AnswerReceived += OnAnswerReceived;
        _sdns = new ServiceDiscovery(_mdns);
        _mdns.Start();
        _staleTimer.Start();
        Advertise();
        _sdns.QueryServiceInstances(new DomainName(ServiceName));
    }

    /// <summary>团队列表变化后重新广播（TXT 里的 teams 更新）。</summary>
    public void RefreshAdvertisement()
    {
        if (!_running) return;
        try
        {
            Advertise();
            _sdns?.QueryServiceInstances(new DomainName(ServiceName));
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // mDNS 失效不致命，降级为手动地址模式
        }
    }

    /// <summary>
    /// 主动发起一轮 mDNS 查询并等待应答落缓存。应答是异步到达的（OnAnswerReceived），
    /// 需要短码换取等"先发现后连接"的场景必须在遍历 peer 前调用本方法刷新在线列表。
    /// </summary>
    public async Task QueryPeersAsync(int waitMs = 2500)
    {
        if (!_running || _sdns is null) return;
        try
        {
            _sdns.QueryServiceInstances(new DomainName(ServiceName));
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return;
        }
        await Task.Delay(waitMs).ConfigureAwait(false);
    }

    private void Advertise()
    {
        if (_sdns is null || _mdns is null) return;
        if (_profile is not null)
        {
            try { _sdns.Unadvertise(_profile); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }
        var port = 0; // 由上层注入实际端口：通过团队 ID 提供器之外的字段
        if (ServicePort > 0) port = ServicePort;
        if (port == 0) return;
        _profile = new ServiceProfile(_identity.MachineCode, ServiceName, (ushort)port, GetAdvertiseAddresses());
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        _profile.AddProperty("mc", _identity.MachineCode);
        _profile.AddProperty("nm", _identity.Nickname);
        _profile.AddProperty("teams", string.Join(',', _teamIdProvider()));
        if (ServiceHttpsPort > 0) _profile.AddProperty("hp", ServiceHttpsPort.ToString());
        _profile.AddProperty("ts", timestamp);
        _profile.AddProperty("sig", _identity.Sign($"adv|{_identity.MachineCode}|{timestamp}"));
        _sdns.Advertise(_profile);
    }

    /// <summary>服务端口由上层（TeamShareService）在 Start 前设置。</summary>
    public int ServicePort { get; set; }

    /// <summary>HTTPS 服务端口由上层在 Start 后设置（0 = 未启用，不广播 hp）。</summary>
    public int ServiceHttpsPort { get; set; }

    private static IEnumerable<IPAddress> GetAdvertiseAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                    && n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        try
        {
            var message = e.Message;
            SRVRecord? srv = null;
            TXTRecord? txt = null;
            IPAddress? address = null;
            var isGoodbye = false;

            foreach (var answer in message.Answers)
            {
                switch (answer)
                {
                    case SRVRecord record:
                        srv = record;
                        break;
                    case TXTRecord record:
                        txt = record;
                        break;
                    case ARecord record:
                        address = record.Address;
                        break;
                    case AAAARecord record:
                        address ??= record.Address;
                        break;
                }
                if (answer.TTL == TimeSpan.Zero) isGoodbye = true;
            }

            if (srv is null || txt is null) return;
            lock (_sync) { _answerTotal++; }
            if (!srv.Name.ToString().Contains(ServiceName, StringComparison.Ordinal))
            {
                lock (_sync) { _answerNameMismatch++; }
                return;
            }

            var properties = ParseTxtProperties(txt.Strings);
            if (properties is null || !properties.TryGetValue("mc", out var machineCode)) return;
            if (machineCode == _identity.MachineCode) // 忽略本机回环（同机多实例机器码相同也会走到这里）
            {
                lock (_sync) { _answerSelfIgnored++; }
                return;
            }

            PeerInfo peer;
            bool changed;
            lock (_sync)
            {
                var exists = _peers.TryGetValue(machineCode, out var existing);
                peer = exists ? existing! : new PeerInfo { MachineCode = machineCode };
                peer.Nickname = properties.TryGetValue("nm", out var nickname) ? nickname : machineCode;
                peer.Port = srv.Port;
                peer.HttpsPort = properties.TryGetValue("hp", out var httpsPort) && int.TryParse(httpsPort, out var hp)
                    ? hp : 0;
                peer.Ip = address?.ToString() ?? peer.Ip;
                peer.Teams = properties.TryGetValue("teams", out var teams)
                    ? teams.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    : [];
                peer.LastSeen = DateTimeOffset.UtcNow;
                var wasOnline = peer.Online;
                peer.Online = !isGoodbye;
                if (!exists) _peers[machineCode] = peer;
                changed = !exists || wasOnline != peer.Online;
            }
            if (changed) PeersChanged?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            // 解析失败忽略
        }
    }

    private static Dictionary<string, string> ParseTxtProperties(IEnumerable<string> strings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in strings)
        {
            var index = item.IndexOf('=');
            if (index <= 0) continue;
            result[item[..index]] = item[(index + 1)..];
        }
        return result;
    }

    private void MarkStalePeers()
    {
        bool changed = false;
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-StaleSeconds);
        lock (_sync)
        {
            foreach (var peer in _peers.Values)
            {
                if (peer.Online && peer.LastSeen < cutoff)
                {
                    peer.Online = false;
                    changed = true;
                }
            }
        }
        if (changed) PeersChanged?.Invoke();
    }

    public void Dispose()
    {
        _staleTimer.Dispose();
        if (_sdns is not null && _profile is not null)
        {
            try { _sdns.Unadvertise(_profile); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }
        _mdns?.Stop();
        _mdns?.Dispose();
        _sdns?.Dispose();
        _running = false;
    }
}
