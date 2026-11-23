using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using KBEA.Core.Team;
using KBEA.Core.Storage;
using KBEA.App.Services;

namespace KBEA.App.Views;

/// <summary>
/// 局域网诊断面板：直观显示 mDNS 发现链路各环节状态——
/// 本机广播（IP/端口）、mDNS 应答统计（收不到=组播被拦/子网隔离；收到但被过滤=名字/回环）、
/// 发现的设备列表（在线状态 + 服务端口 TCP 连通性测试）、本机网卡（多网卡/AP 隔离线索）。
/// </summary>
public partial class LanDiagnosticsWindow : Window
{
    private readonly TeamShareService _team;

    public LanDiagnosticsWindow(TeamShareService team)
    {
        InitializeComponent();
        _team = team;
        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>设备行 VM：peer 信息 + 端口连通性测试结果。</summary>
    public sealed class LanDeviceRow
    {
        public string Nickname { get; set; } = "";
        public bool Online { get; set; }
        public string OnlineText => Online ? "● 在线" : "○ 离线";
        public Brush OnlineBrush => Online
            ? new SolidColorBrush(Color.FromRgb(0x14, 0x80, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB7));
        public string DetailText { get; set; } = "";
        public string Ip { get; set; } = "";
        public int Port { get; set; }
        public string PortStatusText { get; set; } = "端口检测中…";
        public Brush PortBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB7));
    }

    private async Task RefreshAsync()
    {
        // 1) 本机广播状态 + 应答统计 + 网卡
        var diag = _team.GetLanDiagnostics();
        SelfInfoText.Text = diag.Running
            ? $"本机 {_team.Identity.Nickname}（机器码 {_team.Identity.MachineCode[..8]}…）正在广播：服务端口 {diag.ServicePort}，HTTPS {diag.HttpsPort}，广播 IP：{(diag.AdvertiseIps.Count > 0 ? string.Join("、", diag.AdvertiseIps) : "无（网卡过滤后为空！）")}"
            : "mDNS 服务未运行";
        StatsText.Text = $"mDNS 应答统计：收到 {diag.AnswerTotal} 条（有效 peer 计入设备列表；本机回环忽略 {diag.AnswerSelfIgnored}、服务名不符 {diag.AnswerNameMismatch}）——收到 0 条通常意味着组播被路由器/VPN 拦截或不在同一子网";
        InterfaceList.ItemsSource = diag.Interfaces;

        // 2) 主动扫描一轮 + 等待应答
        HintText.Text = "扫描中…";
        await _team.ScanLanAsync(2500).ConfigureAwait(true);

        // 3) 设备列表 + TCP 连通性
        var rows = new ObservableCollection<LanDeviceRow>();
        foreach (var peer in _team.Peers)
        {
            var row = new LanDeviceRow
            {
                Nickname = peer.Nickname,
                Online = peer.Online,
                DetailText = $"机器码 {peer.MachineCode[..Math.Min(10, peer.MachineCode.Length)]}… | IP {peer.Ip} | 端口 {peer.Port} | 最后现身 {peer.LastSeen:HH:mm:ss}",
                Ip = peer.Ip,
                Port = peer.Port,
            };
            rows.Add(row);
        }
        DeviceList.ItemsSource = rows;
        NoDeviceText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var row in rows)
        {
            var (ok, note) = await TestPortAsync(row.Ip, row.Port).ConfigureAwait(true);
            row.PortStatusText = ok ? $"服务端口 {row.Port} 连通 ✓" : $"服务端口 {row.Port} 不通 ✗（{note}）";
            row.PortBrush = ok
                ? new SolidColorBrush(Color.FromRgb(0x14, 0x80, 0x4A))
                : new SolidColorBrush(Color.FromRgb(0xB5, 0x47, 0x08));
            DeviceList.Items.Refresh();
        }
        HintText.Text = rows.Count == 0
            ? "提示：两台机器都打开本面板互相对照——若双方都显示 0 条应答，多半是路由器 AP 隔离/访客网络隔离组播，或 VPN 虚拟网卡干扰。"
            : "";
    }

    private async Task<(bool Ok, string Note)> TestPortAsync(string ip, int port)
    {
        if (string.IsNullOrWhiteSpace(ip) || port <= 0) return (false, "无 IP 或端口");
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(ip, port);
            var done = await Task.WhenAny(connect, Task.Delay(1500)).ConfigureAwait(true);
            if (done != connect) return (false, "超时 1.5s");
            return (client.Connected, "");
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return (false, ex.Message.Length > 60 ? ex.Message[..60] : ex.Message);
        }
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await RefreshAsync().ConfigureAwait(true);
    private async void OnRetest(object sender, RoutedEventArgs e) => await RefreshAsync().ConfigureAwait(true);
}
