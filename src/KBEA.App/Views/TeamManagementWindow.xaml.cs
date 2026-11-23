using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KBEA.App.Services;
using KBEA.Core.Models;
using KBEA.Core.Team;
using KBEA.App.ViewModels;

namespace KBEA.App.Views;

/// <summary>
/// 团队管理窗口：新建/加入团队、成员与 owner 信息、本机共享给各团队的空间与权限、邀请码。
/// 共享操作直接落到 teamService（写入 spaces.jsonl + 触发导出同步）。
/// </summary>
public partial class TeamManagementWindow : Window
{
    public TeamManagementWindow(TeamShareService teamService, AppConfiguration configuration, IDialogService dialogs)
    {
        InitializeComponent();
        DataContext = new TeamManagerViewModel(teamService, configuration, dialogs);
        Loaded += (_, _) => ((TeamManagerViewModel)DataContext).Refresh();
    }
}

/// <summary>团队管理窗口 VM。</summary>
public partial class TeamManagerViewModel : ObservableObject
{
    private readonly TeamShareService _team;
    private readonly AppConfiguration _configuration;
    private readonly IDialogService _dialogs;

    public TeamManagerViewModel(TeamShareService teamService, AppConfiguration configuration, IDialogService dialogs)
    {
        _team = teamService;
        _configuration = configuration;
        _dialogs = dialogs;
        CreateTeamCommand = new RelayCommand(CreateTeam);
        JoinTeamCommand = new AsyncRelayCommand(JoinTeamAsync);
        CopyTeamIdCommand = new RelayCommand(CopyTeamId, () => SelectedTeam is not null);
        AddShareCommand = new RelayCommand(AddShare, () => SelectedTeam is not null);
        RemoveShareCommand = new RelayCommand<ShareRow>(RemoveShare, row => SelectedTeam is not null);
        GenerateInviteCommand = new RelayCommand(GenerateInvite, () => SelectedTeam is not null && SelectedTeam.IsOwner);
        LeaveTeamCommand = new RelayCommand(LeaveTeam, () => SelectedTeam is not null);
        FirewallCheckCommand = new RelayCommand(() => ShowFirewallGuideIfNeeded(manual: true));
        LanDiagnosticsCommand = new RelayCommand(OpenLanDiagnostics);
    }

    public ObservableCollection<TeamCardItem> Teams { get; } = [];
    public ObservableCollection<TeamMemberItem> DetailMembers { get; } = [];
    public ObservableCollection<ShareRow> DetailShares { get; } = [];

    [ObservableProperty] private TeamCardItem? _selectedTeam;
    [ObservableProperty] private string _detailTitle = "未选择团队";
    [ObservableProperty] private bool _detailSharesEmpty = true;

    public RelayCommand CreateTeamCommand { get; }
    public AsyncRelayCommand JoinTeamCommand { get; }
    public RelayCommand CopyTeamIdCommand { get; }
    public RelayCommand AddShareCommand { get; }
    public RelayCommand<ShareRow> RemoveShareCommand { get; }
    public RelayCommand GenerateInviteCommand { get; }
    public RelayCommand LeaveTeamCommand { get; }
    public RelayCommand FirewallCheckCommand { get; }
    public RelayCommand LanDiagnosticsCommand { get; }

    partial void OnSelectedTeamChanged(TeamCardItem? value) => RefreshDetail();

    /// <summary>
    /// CommunityToolkit.Mvvm 8.x 的 RelayCommand 不再挂 CommandManager.RequerySuggested，
    /// CanExecute 变化必须手动通知，否则按钮会停留在初始禁用态。
    /// </summary>
    private void NotifyCommands()
    {
        CopyTeamIdCommand.NotifyCanExecuteChanged();
        AddShareCommand.NotifyCanExecuteChanged();
        RemoveShareCommand.NotifyCanExecuteChanged();
        GenerateInviteCommand.NotifyCanExecuteChanged();
        LeaveTeamCommand.NotifyCanExecuteChanged();
    }

    /// <summary>从盘上重载团队列表（服务端可能刚收到新成员）。</summary>
    public void Refresh()
    {
        _team.ReloadTeams();
        var peers = _team.Peers.ToDictionary(p => p.MachineCode, p => p.Online);
        var previousId = SelectedTeam?.TeamId;
        Teams.Clear();
        foreach (var team in _team.Teams)
        {
            var members = team.Members
                .Select(m => new TeamMemberItem(m, peers.GetValueOrDefault(m.MachineCode)))
                .OrderByDescending(m => m.Member.IsOwner)
                .ToList();
            var ownerNickname = team.Members.FirstOrDefault(m => m.IsOwner)?.Nickname ?? team.Definition.OwnerMachineCode;
            Teams.Add(new TeamCardItem(team, team.IsOwner(_team.Identity.MachineCode), ownerNickname, members));
        }
        SelectedTeam = Teams.FirstOrDefault(t => t.TeamId == previousId) ?? Teams.FirstOrDefault();
        RefreshDetail();
        NotifyCommands();
    }

    private void RefreshDetail()
    {
        DetailMembers.Clear();
        DetailShares.Clear();
        if (SelectedTeam is null)
        {
            DetailTitle = "未选择团队";
            DetailSharesEmpty = true;
            NotifyCommands();
            return;
        }
        DetailTitle = SelectedTeam.Name;
        foreach (var member in SelectedTeam.Members) DetailMembers.Add(member);
        foreach (var share in SelectedTeam.Team.MyShares.OrderByDescending(s => s.Level)) DetailShares.Add(MakeShareRow(share));
        DetailSharesEmpty = DetailShares.Count == 0;
        NotifyCommands();
    }

    /// <summary>共享行：级别直接在行内切换（按空间配权限，全团队同一级别）。</summary>
    private ShareRow MakeShareRow(SpaceShareDeclaration share) => new(share, ChangeShareLevel);

    private void ChangeShareLevel(ShareRow row)
    {
        if (SelectedTeam is null) return;
        var shares = SelectedTeam.Team.MyShares
            .Select(s => s.SpaceId == row.Share.SpaceId
                ? new SpaceShareDeclaration { SpaceId = s.SpaceId, SpaceName = s.SpaceName, TeamId = s.TeamId, Level = row.Level }
                : s)
            .ToList();
        _team.SetShares(SelectedTeam.TeamId, shares);
        // 事件处理中重建集合（清空 DetailShares）会踩 ItemsControl 容器竞态，推迟到事件循环外
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(Refresh);
    }

    private void CreateTeam()
    {
        var name = _dialogs.Prompt("新建团队", "团队名称", Environment.UserName + " 的团队");
        if (string.IsNullOrWhiteSpace(name)) return;
        _team.CreateTeam(name.Trim());
        // 静默创建：不弹提示、不自动进共享配置——共享空间/邀请码由用户自行操作
        Refresh();
    }

    /// <summary>防火墙检测：手动点击时反馈检测结果；自动调用（新建团队/邀请前）规则齐全时静默跳过。</summary>
    private void ShowFirewallGuideIfNeeded(bool manual = false)
    {
        try
        {
            var port = _team.ServerPort;
            if (port == 0) return;
            if (FirewallGuide.RulesReady(port) && FirewallGuide.ServiceRuleMatches(port))
            {
                if (manual)
                    _dialogs.Info($"防火墙规则已就绪：mDNS 5353/UDP 与服务端口 {port}-{port + 5}/TCP 放行规则均存在，其他设备可以发现并连接本机。");
                return;
            }
            var guide = new FirewallGuideWindow(port) { Owner = System.Windows.Application.Current.MainWindow };
            guide.ShowDialog();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            // 检查失败不阻塞团队创建流程
        }
    }

    /// <summary>局域网诊断面板：可视化查看 mDNS 发现链路与端口连通性。</summary>
    private void OpenLanDiagnostics()
    {
        var win = new LanDiagnosticsWindow(_team) { Owner = System.Windows.Application.Current.MainWindow };
        win.ShowDialog();
    }

    private void AddShare()
    {
        if (SelectedTeam is null) return;
        // 团队引用空间（TeamLink）不允许再共享：过滤 IsShareable
        var shareable = _configuration.Spaces.Where(s => s.IsShareable && !string.IsNullOrWhiteSpace(s.Path) && System.IO.Directory.Exists(s.Path)).ToList();
        if (shareable.Count == 0)
        {
            _dialogs.Info("没有可共享的本地空间。请先在「工作空间」面板创建空间。");
            return;
        }
        var dialog = new ShareSpaceWindow(shareable) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.SelectedSpace is null) return;

        var shares = SelectedTeam.Team.MyShares.Where(s => s.SpaceId != dialog.SelectedSpace.Id).ToList();
        shares.Add(new SpaceShareDeclaration
        {
            SpaceId = dialog.SelectedSpace.Id,
            SpaceName = dialog.SelectedSpace.Name,
            TeamId = SelectedTeam.TeamId,
            Level = dialog.SelectedLevel
        });
        _team.SetShares(SelectedTeam.TeamId, shares);
        Refresh();
        _dialogs.Info($"已把「{dialog.SelectedSpace.Name}」以 {LevelText(dialog.SelectedLevel)} 共享给团队「{SelectedTeam.Name}」。\n\n元数据将随下次同步广播给团队成员；权限级别可随时在列表里直接切换。");
    }

    private void RemoveShare(ShareRow? row)
    {
        if (SelectedTeam is null || row is null) return;
        if (!_dialogs.Confirm($"停止向团队「{SelectedTeam.Name}」共享「{row.Share.SpaceName}」？")) return;
        var shares = SelectedTeam.Team.MyShares.Where(s => s.SpaceId != row.Share.SpaceId).ToList();
        _team.SetShares(SelectedTeam.TeamId, shares);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(Refresh);
    }

    private async Task JoinTeamAsync()
    {
        var code = _dialogs.Prompt("加入团队", "粘贴邀请码（KBEA-XXXX-XXXX）");
        if (string.IsNullOrWhiteSpace(code)) return;
        // 加入方同样要收 mDNS 应答（inbound 5353）：规则缺失时先引导放行
        ShowFirewallGuideIfNeeded();
        var (success, message) = await _team.JoinTeamAsync(code.Trim());
        _dialogs.Info(message, success ? "加入团队" : "加入失败");
        if (success) Refresh();
    }

    private void CopyTeamId()
    {
        if (SelectedTeam is null) return;
        ClipboardHelper.TrySetText(SelectedTeam.TeamId);
    }

    private void GenerateInvite()
    {
        if (SelectedTeam is null) return;
        // 邀请前确保防火墙放行：否则对方无法发现/连接本机（规则齐全时静默跳过）
        ShowFirewallGuideIfNeeded();
        var code = _team.GenerateInviteCode(SelectedTeam.Team);
        // 短码很短，剪贴板几乎不会竞争超时；失败也不影响功能
        var copied = ClipboardHelper.TrySetText(code);
        _dialogs.Info($"邀请码已生成（7 天内有效，重新生成会使旧码失效）" + (copied ? "，已复制到剪贴板：" : "：") + $"\n\n{code}\n\n把它发给同事——对方在「加入团队」里粘贴即可，你和对方都要保持在线。");
    }

    /// <summary>owner = 解散团队（成员失去成员表同步与邀请入口）；成员 = 退出团队。</summary>
    private void LeaveTeam()
    {
        if (SelectedTeam is null) return;
        if (SelectedTeam.IsOwner)
        {
            if (!_dialogs.Confirm($"确定解散团队「{SelectedTeam.Name}」？\n\n所有成员将失去成员表同步与新邀请入口（各成员本地副本数据保留）。本机团队数据将被删除。")) return;
        }
        else
        {
            if (!_dialogs.Confirm($"确定退出团队「{SelectedTeam.Name}」？本地团队数据将被删除。")) return;
        }
        _team.LeaveTeam(SelectedTeam.TeamId);
        Refresh();
    }

    private static string LevelText(ShareLevel level) => level switch
    {
        ShareLevel.Search => "可搜索",
        ShareLevel.Read => "可读取",
        ShareLevel.Write => "可编辑",
        _ => level.ToString()
    };
}

/// <summary>共享列表行：级别可编辑（按空间，全团队同一级别）。</summary>
public sealed class ShareRow : ObservableObject
{
    private readonly Action<ShareRow> _onChanged;

    public ShareRow(SpaceShareDeclaration share, Action<ShareRow> onChanged)
    {
        Share = share;
        _onChanged = onChanged;
    }

    public SpaceShareDeclaration Share { get; }
    public string SpaceName => Share.SpaceName;
    public ShareLevel Level => Share.Level;

    /// <summary>ComboBox SelectedIndex 绑定；变更即落盘。</summary>
    public int LevelIndex
    {
        get => (int)Share.Level;
        set
        {
            if ((int)Share.Level == value) return;
            Share.Level = (ShareLevel)value;
            OnPropertyChanged();
            _onChanged(this);
        }
    }
}
